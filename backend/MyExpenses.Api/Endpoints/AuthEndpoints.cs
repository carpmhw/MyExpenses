using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;
using MyExpenses.Api.Models.Requests;
using MyExpenses.Api.Services;
using OtpNet;

namespace MyExpenses.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var jwtSecret = app.Configuration["Jwt:Secret"] ?? "placeholder-key-replace-in-production";
        var jwtIssuer = app.Configuration["Jwt:Issuer"];
        var jwtAudience = app.Configuration["Jwt:Audience"];

        var publicGroup = app.MapGroup("/api/auth").AllowAnonymous();
        var protectedGroup = app.MapGroup("/api/auth").RequireAuthorization();

        void SetSessionCookie(HttpContext httpContext, IDataProtectionProvider dataProtection, int userId, long jwtExp)
        {
            var protector = dataProtection.CreateProtector("MyExpenses.Session");
            var cookieData = $"{userId}:{jwtExp}";
            var encrypted = Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(cookieData)));
            httpContext.Response.Cookies.Append("mx_session", encrypted, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = !app.Environment.IsDevelopment(),
                MaxAge = TimeSpan.FromMinutes(1440)
            });
        }

        publicGroup.MapGet("/status", async (HttpContext httpContext, AppDbContext db) =>
        {
            var hasUsers = await db.Users.AnyAsync();

            var userIdStr = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userIdStr is null || !int.TryParse(userIdStr, out var userId))
                return Results.Ok(new { authenticated = false, user = (object?)null, hasUsers });

            var user = await db.Users.FindAsync(userId);
            if (user is null)
                return Results.Ok(new { authenticated = false, user = (object?)null, hasUsers });

            return Results.Ok(new
            {
                authenticated = true,
                user = new
                {
                    user.Id,
                    user.Email,
                    user.DisplayName,
                    user.IsTwoFactorEnabled
                },
                hasUsers
            });
        });

        publicGroup.MapPost("/register", async (RegisterRequest request, AppDbContext db, HttpContext httpContext, IDataProtectionProvider dataProtection) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
                return Results.BadRequest(new { message = "Email and password are required" });

            if (await db.Users.AnyAsync())
                return Results.Json(new { message = "User already exists" }, statusCode: 403);

            if (await db.Users.AnyAsync(u => u.Email == request.Email))
                return Results.Conflict(new { message = "Email already exists" });

            var user = new User
            {
                Email = request.Email,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.Email : request.DisplayName,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            db.Users.Add(user);
            await db.SaveChangesAsync();

            var (token, jwtExp) = JwtHelper.GenerateToken(user, jwtSecret, jwtIssuer, jwtAudience);
            SetSessionCookie(httpContext, dataProtection, user.Id, jwtExp);

            return Results.Ok(new
            {
                token,
                user = new
                {
                    user.Id,
                    user.Email,
                    user.DisplayName,
                    user.IsTwoFactorEnabled
                }
            });
        });

        publicGroup.MapPost("/login", async (LoginRequest request, AppDbContext db, HttpContext httpContext, IDataProtectionProvider dataProtection) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
                return Results.BadRequest(new { message = "Email and password are required" });

            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
            if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
                return Results.Json(new { message = "Invalid email or password" }, statusCode: 401);

            if (user.IsTwoFactorEnabled)
            {
                var tempToken = JwtHelper.GenerateTempToken(user.Id, jwtSecret, jwtIssuer, jwtAudience);
                return Results.Ok(new { requiresTwoFactor = true, tempToken });
            }

            var (token, jwtExp) = JwtHelper.GenerateToken(user, jwtSecret, jwtIssuer, jwtAudience);
            SetSessionCookie(httpContext, dataProtection, user.Id, jwtExp);

            return Results.Ok(new
            {
                token,
                user = new
                {
                    user.Id,
                    user.Email,
                    user.DisplayName,
                    user.IsTwoFactorEnabled
                }
            });
        });

        publicGroup.MapPost("/2fa/login", async (TwoFactorLoginRequest request, AppDbContext db, HttpContext httpContext, IDataProtectionProvider dataProtection) =>
        {
            if (string.IsNullOrWhiteSpace(request.TempToken) || string.IsNullOrWhiteSpace(request.Code))
                return Results.BadRequest(new { message = "TempToken and code are required" });

            var userId = JwtHelper.ValidateTempToken(request.TempToken, jwtSecret, jwtIssuer, jwtAudience);
            if (userId is null)
                return Results.Json(new { message = "Invalid or expired temp token" }, statusCode: 401);

            var user = await db.Users.FindAsync(userId.Value);
            if (user is null || !user.IsTwoFactorEnabled || string.IsNullOrEmpty(user.TotpSecret))
                return Results.Json(new { message = "Invalid request" }, statusCode: 401);

            var secretBytes = Base32Encoding.ToBytes(user.TotpSecret);
            var totp = new Totp(secretBytes);

            if (totp.VerifyTotp(request.Code, out long _))
            {
                var (token, jwtExp) = JwtHelper.GenerateToken(user, jwtSecret, jwtIssuer, jwtAudience);
                SetSessionCookie(httpContext, dataProtection, user.Id, jwtExp);
                return Results.Ok(new
                {
                    token,
                    user = new
                    {
                        user.Id,
                        user.Email,
                        user.DisplayName,
                        user.IsTwoFactorEnabled
                    }
                });
            }

            return Results.Json(new { message = "Invalid verification code" }, statusCode: 401);
        });

        publicGroup.MapPost("/2fa/recovery-login", async (RecoveryCodeLoginRequest request, AppDbContext db, HttpContext httpContext, IDataProtectionProvider dataProtection) =>
        {
            if (string.IsNullOrWhiteSpace(request.TempToken) || string.IsNullOrWhiteSpace(request.RecoveryCode))
                return Results.BadRequest(new { message = "TempToken and recovery code are required" });

            var userId = JwtHelper.ValidateTempToken(request.TempToken, jwtSecret, jwtIssuer, jwtAudience);
            if (userId is null)
                return Results.Json(new { message = "Invalid or expired temp token" }, statusCode: 401);

            var user = await db.Users.FindAsync(userId.Value);
            if (user is null || !user.IsTwoFactorEnabled || string.IsNullOrEmpty(user.RecoveryCodes))
                return Results.Json(new { message = "Invalid request" }, statusCode: 401);

            var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(request.RecoveryCode.Trim().ToUpperInvariant()));
            var codeHashStr = Convert.ToHexString(codeHash);

            var codes = System.Text.Json.JsonSerializer.Deserialize<List<RecoveryCodeEntry>>(user.RecoveryCodes) ?? [];

            var match = codes.FirstOrDefault(c => c.Hash == codeHashStr && !c.Used);
            if (match is null)
                return Results.Json(new { message = "Invalid recovery code" }, statusCode: 401);

            match.Used = true;
            user.RecoveryCodes = System.Text.Json.JsonSerializer.Serialize(codes);
            await db.SaveChangesAsync();

            var (token, jwtExp) = JwtHelper.GenerateToken(user, jwtSecret, jwtIssuer, jwtAudience);
            SetSessionCookie(httpContext, dataProtection, user.Id, jwtExp);
            return Results.Ok(new
            {
                token,
                user = new
                {
                    user.Id,
                    user.Email,
                    user.DisplayName,
                    user.IsTwoFactorEnabled
                }
            });
        });

        protectedGroup.MapPost("/2fa/setup", async (AppDbContext db, HttpContext httpContext) =>
        {
            var userId = int.Parse(httpContext.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var user = await db.Users.FindAsync(userId);
            if (user is null)
                return Results.NotFound();

            var secret = KeyGeneration.GenerateRandomKey(20);
            var secretBase32 = Base32Encoding.ToString(secret);
            var provisioningUri = new OtpUri(OtpType.Totp, secret, user.Email, "MyExpenses").ToString();

            user.TotpSecret = secretBase32;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { secret = secretBase32, uri = provisioningUri });
        });

        protectedGroup.MapPost("/2fa/verify", async (VerifyTwoFactorRequest request, AppDbContext db, HttpContext httpContext) =>
        {
            var userId = int.Parse(httpContext.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var user = await db.Users.FindAsync(userId);
            if (user is null || string.IsNullOrEmpty(user.TotpSecret))
                return Results.BadRequest(new { message = "2FA setup not initiated" });

            var secretBytes = Base32Encoding.ToBytes(user.TotpSecret);
            var totp = new Totp(secretBytes);

            if (!totp.VerifyTotp(request.Code, out long _))
                return Results.BadRequest(new { message = "Invalid verification code" });

            var recoveryCodes = GenerateRecoveryCodes();
            var codeHashes = recoveryCodes.Select(code => new RecoveryCodeEntry
            {
                Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))),
                Used = false,
            }).ToList();

            user.IsTwoFactorEnabled = true;
            user.RecoveryCodes = System.Text.Json.JsonSerializer.Serialize(codeHashes);
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { enabled = true, recoveryCodes });
        });

        protectedGroup.MapPost("/2fa/disable", async (AppDbContext db, HttpContext httpContext) =>
        {
            var userId = int.Parse(httpContext.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var user = await db.Users.FindAsync(userId);
            if (user is null)
                return Results.NotFound();

            user.IsTwoFactorEnabled = false;
            user.TotpSecret = null;
            user.RecoveryCodes = null;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { disabled = true });
        });

        protectedGroup.MapPut("/profile", async (UpdateProfileRequest request, AppDbContext db, HttpContext httpContext) =>
        {
            var userId = int.Parse(httpContext.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var user = await db.Users.FindAsync(userId);
            if (user is null)
                return Results.NotFound();

            user.DisplayName = request.DisplayName;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                user.Id,
                user.Email,
                user.DisplayName,
                user.IsTwoFactorEnabled
            });
        });

        protectedGroup.MapPut("/password", async (ChangePasswordRequest request, AppDbContext db, HttpContext httpContext) =>
        {
            var userId = int.Parse(httpContext.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var user = await db.Users.FindAsync(userId);
            if (user is null)
                return Results.NotFound();

            if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
                return Results.BadRequest(new { message = "Current password is incorrect" });

            if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
                return Results.BadRequest(new { message = "New password must be at least 6 characters" });

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
            user.TokenVersion++;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { message = "Password updated successfully" });
        });

        protectedGroup.MapPost("/logout-all", async (AppDbContext db, HttpContext httpContext) =>
        {
            var userId = int.Parse(httpContext.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var user = await db.Users.FindAsync(userId);
            if (user is null)
                return Results.NotFound();

            user.TokenVersion++;
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { message = "Logged out of all devices" });
        });

        protectedGroup.MapGet("/2fa/recovery-codes", async (AppDbContext db, HttpContext httpContext) =>
        {
            var userId = int.Parse(httpContext.User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var user = await db.Users.FindAsync(userId);
            if (user is null)
                return Results.NotFound();

            var recoveryCodes = GenerateRecoveryCodes();
            var codeHashes = recoveryCodes.Select(code => new RecoveryCodeEntry
            {
                Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))),
                Used = false,
            }).ToList();

            user.RecoveryCodes = System.Text.Json.JsonSerializer.Serialize(codeHashes);
            user.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { recoveryCodes });
        });

        protectedGroup.MapPost("/api-tokens", async (CreateApiTokenRequest request, AppDbContext db, ClaimsPrincipal user) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest("Token name is required");

            var userId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var tokenValue = "oc_" + Convert.ToHexStringLower(tokenBytes);

            var apiToken = new ApiToken
            {
                UserId = userId,
                Name = request.Name,
                TokenHash = BCrypt.Net.BCrypt.HashPassword(tokenValue),
                Prefix = tokenValue[..12],
                Scopes = request.Scopes is not null ? JsonSerializer.Serialize(request.Scopes) : null,
                CreatedAt = DateTime.UtcNow
            };

            db.ApiTokens.Add(apiToken);
            await db.SaveChangesAsync();

            return Results.Created($"/api/auth/api-tokens/{apiToken.Id}", new
            {
                id = apiToken.Id,
                name = apiToken.Name,
                prefix = apiToken.Prefix,
                createdAt = apiToken.CreatedAt,
                scopes = request.Scopes,
                token = tokenValue
            });
        });

        protectedGroup.MapGet("/api-tokens", async (AppDbContext db, ClaimsPrincipal user) =>
        {
            var userId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var tokens = await db.ApiTokens
                .Where(t => t.UserId == userId)
                .ToListAsync();
            return Results.Ok(tokens.Select(t => new
            {
                t.Id,
                t.Name,
                t.Prefix,
                Scopes = t.Scopes != null ? JsonSerializer.Deserialize<string[]>(t.Scopes) : null,
                t.CreatedAt,
                t.LastUsedAt,
                t.ExpiresAt,
                t.IsRevoked
            }));
        });

        protectedGroup.MapDelete("/api-tokens/{id:int}", async (int id, AppDbContext db, ClaimsPrincipal user) =>
        {
            var userId = int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var token = await db.ApiTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId);
            if (token is null) return Results.NotFound();

            token.IsRevoked = true;
            await db.SaveChangesAsync();
            return Results.NoContent();
        })
        .RequireAuthorization();

        publicGroup.MapPost("/logout", (HttpContext httpContext) =>
        {
            httpContext.Response.Cookies.Append("mx_session", "", new CookieOptions
            {
                Expires = DateTimeOffset.UnixEpoch,
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = !app.Environment.IsDevelopment(),
                Path = "/"
            });
            return Results.Ok(new { message = "Logged out" });
        });
    }

    private static List<string> GenerateRecoveryCodes()
    {
        var codes = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var randomBytes = RandomNumberGenerator.GetBytes(6);
            var code = string.Join("-",
                Convert.ToHexString(randomBytes[..2]),
                Convert.ToHexString(randomBytes[2..4]),
                Convert.ToHexString(randomBytes[4..]));
            codes.Add(code);
        }
        return codes;
    }
}

public class RecoveryCodeEntry
{
    public string Hash { get; set; } = string.Empty;
    public bool Used { get; set; }
}
