using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MyExpenses.Api.Converters;
using MyExpenses.Api.Data;
using MyExpenses.Api.Endpoints;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")
        ?? "Data Source=MyExpenses.db"));

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

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
#pragma warning disable ASP0000
    var timeZoneService = builder.Services.BuildServiceProvider().GetRequiredService<TimeZoneService>();
    options.SerializerOptions.Converters.Add(new UtcToLocalDateTimeConverter(timeZoneService));
#pragma warning restore ASP0000
});

builder.Services.AddHttpClient();
builder.Services.AddOpenApi();
builder.Services.Configure<TimeZoneOptions>(
    builder.Configuration.GetSection(TimeZoneOptions.SectionName));
builder.Services.AddSingleton<TimeZoneService>();
builder.Services.AddHostedService<SnapshotBackgroundService>();
builder.Services.AddHostedService<StockPriceUpdateService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    await DbInitializer.SeedReferenceDataAsync(db);
    if (app.Environment.IsDevelopment())
    {
        await DbInitializer.SeedSampleDataAsync(db);
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseAuthentication();
app.UseMiddleware<ApiTokenAuthMiddleware>();
app.UseMiddleware<SessionCookieMiddleware>();
app.UseMiddleware<ApiTokenScopeMiddleware>();
app.UseAuthorization();

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

app.Run();
