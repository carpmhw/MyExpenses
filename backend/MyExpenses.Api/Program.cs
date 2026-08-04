using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;

var builder = WebApplication.CreateBuilder(args);

var deploymentOptions = new DeploymentOptions();
builder.Configuration
    .GetSection(DeploymentOptions.SectionName)
    .Bind(deploymentOptions);
if (builder.Configuration.GetValue<bool?>("Auth:CookieSecure") is bool configuredCookieSecure)
{
    deploymentOptions.SecureCookies = configuredCookieSecure;
}
DeploymentOptionsValidator.ThrowIfInvalid(deploymentOptions);
// 讓 endpoint 與 strongly typed deployment validation 使用同一個 cookie security 結果。
builder.Configuration["Auth:CookieSecure"] = deploymentOptions.SecureCookies.ToString();
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(deploymentOptions));
builder.Services.AddTrustedForwardedHeaders(deploymentOptions);
builder.Services.AddDeploymentSecurity(deploymentOptions);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=MyExpenses.db";

var dataProtectionKeyDirectory = builder.Configuration["DataProtection:KeyDirectory"];
if (string.IsNullOrWhiteSpace(dataProtectionKeyDirectory))
{
    dataProtectionKeyDirectory = Path.Combine(builder.Environment.ContentRootPath, "keys");
}

var dataProtectionOptions = new PersistentDataProtectionOptions
{
    ApplicationName = builder.Configuration["DataProtection:ApplicationName"] ?? "MyExpenses",
    KeyDirectory = dataProtectionKeyDirectory,
};
DataProtectionRegistration.Add(
    builder.Services,
    dataProtectionOptions,
    builder.Environment.IsProduction());

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

var sqliteConnection = new SqliteConnectionStringBuilder(connectionString);
var databasePath = sqliteConnection.DataSource;
var backupDirectory = builder.Configuration["SqliteBackup:BackupDirectory"]
    ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".", "backups");
var backupOptions = new SqliteBackupOptions
{
    DatabasePath = databasePath,
    BackupDirectory = backupDirectory,
    RetentionLimit = builder.Configuration.GetValue("SqliteBackup:RetentionLimit", 7),
};
builder.Services.AddSingleton(new SqliteBackupService(backupOptions));
builder.Services.AddSingleton<DatabaseStartupCoordinator>();
// 將 coordinator 的唯讀 readiness 狀態提供給匿名 readiness endpoint。
builder.Services.AddSingleton<IStartupReadiness>(services =>
    services.GetRequiredService<DatabaseStartupCoordinator>());
builder.Services.AddDeploymentHealthChecks();

var jwtSecret = JwtSecretProvider.GetJwtSecret(builder.Configuration, builder.Environment);
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
var jwtAudience = builder.Configuration["Jwt:Audience"];

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = jwtIssuer != null,
            ValidIssuer = jwtIssuer,
            ValidateAudience = jwtAudience != null,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtSecret))
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async ctx =>
            {
                var userIdClaim = ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (userIdClaim is null || !int.TryParse(userIdClaim, out var userId))
                {
                    ctx.Fail("Invalid token claims");
                    return;
                }

                var tokenVersionClaim = ctx.Principal?.FindFirst("tokenVersion")?.Value;
                if (tokenVersionClaim is null || !int.TryParse(tokenVersionClaim, out var tokenVersion))
                {
                    ctx.Fail("Invalid token version");
                    return;
                }

                var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var user = await db.Users.FindAsync(userId);
                if (user is null || user.TokenVersion != tokenVersion)
                {
                    ctx.Fail("Token version mismatch");
                    return;
                }

                var expClaim = ctx.Principal?.FindFirst("exp")?.Value;
                if (expClaim is not null && long.TryParse(expClaim, out var jwtExp))
                {
                    ctx.Principal!.Identities.First().AddClaim(
                        new System.Security.Claims.Claim("jwtExp", jwtExp.ToString()));
                }
            },
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddDevelopmentCors(
        builder.Configuration["Cors:DevelopmentOrigin"] ?? "http://localhost:5173");
}
builder.Services.AddRateLimiter(AuthRateLimitPolicy.Configure);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
});

builder.Services.AddHttpClient();
builder.Services.AddOpenApi();
builder.Services.Configure<TimeZoneOptions>(
    builder.Configuration.GetSection(TimeZoneOptions.SectionName));
builder.Services.Configure<BootstrapOptions>(
    builder.Configuration.GetSection(BootstrapOptions.SectionName));
builder.Services.AddSingleton<TimeZoneService>();
builder.Services.AddScoped<InstallmentCommandService>();
builder.Services.AddHostedService<SnapshotBackgroundService>();
builder.Services.AddHostedService<StockPriceUpdateService>();

var app = builder.Build();

app.Services.GetRequiredService<DataProtectionStartupValidator>().Validate();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await SingleOwnerIntegrityPreflight.ValidateAsync(db);
    var ownerCount = await SingleOwnerIntegrityPreflight.GetUserCountAsync(db);
    var bootstrapOptions = scope.ServiceProvider.GetRequiredService<IOptions<BootstrapOptions>>().Value;
    BootstrapSecretProvider.ValidateForStartup(bootstrapOptions, ownerCount > 0, app.Environment);
    var startupCoordinator = scope.ServiceProvider.GetRequiredService<DatabaseStartupCoordinator>();
    var timeZoneService = scope.ServiceProvider.GetRequiredService<TimeZoneService>();
    // 完成 migration 後才 seed reference data，最後由 coordinator 宣告 readiness。
    await startupCoordinator.InitializeAsync(db, async (seedDb, cancellationToken) =>
    {
        await timeZoneService.InitializeAsync(seedDb, cancellationToken);
        await DbInitializer.SeedReferenceDataAsync(seedDb);
        if (app.Environment.IsDevelopment())
            await DbInitializer.SeedSampleDataAsync(seedDb, timeZoneService);
    });
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// forwarded headers 必須先於 rate limiting、authentication 與 authorization 執行。
app.UseForwardedHeaders();
app.UseDeploymentSecurity(deploymentOptions);
if (app.Environment.IsDevelopment())
{
    app.UseCors();
}
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<ApiTokenAuthMiddleware>();
app.UseMiddleware<SessionCookieMiddleware>();
app.UseMiddleware<ApiTokenScopeMiddleware>();
app.UseAuthorization();
app.MapDeploymentHealthChecks();

app.MapCategoryEndpoints();
app.MapTransactionEndpoints();
app.MapInstallmentEndpoints();
app.MapCreditCardEndpoints();
app.MapCreditCardBillEndpoints();
app.MapBankAccountEndpoints();
app.MapStockEndpoints();
app.MapWithdrawalEndpoints();
app.MapPaymentMethodEndpoints();
app.MapReportEndpoints();
app.MapAuthEndpoints();
app.MapSnapshotEndpoints();
app.MapExchangeRateEndpoints();
app.MapSettingsEndpoints();

app.Run();
