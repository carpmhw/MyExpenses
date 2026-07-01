using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;

namespace MyExpenses.Api.Services;

public class ApiTokenAuthMiddleware
{
    private readonly RequestDelegate _next;

    public ApiTokenAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader?.StartsWith("Bearer oc_") == true)
        {
            var tokenValue = authHeader["Bearer ".Length..];
            var prefix = tokenValue[..12];

            var token = await db.ApiTokens
                .FirstOrDefaultAsync(t => t.Prefix == prefix && !t.IsRevoked);

            if (token is null || (token.ExpiresAt.HasValue && token.ExpiresAt < DateTime.UtcNow))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Invalid or revoked API token");
                return;
            }

            if (!BCrypt.Net.BCrypt.Verify(tokenValue, token.TokenHash))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("API token verification failed");
                return;
            }

            token.LastUsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var user = await db.Users.FindAsync(token.UserId);
            if (user is null)
            {
                context.Response.StatusCode = 401;
                return;
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim("displayName", user.DisplayName),
                new Claim("tokenType", "api")
            };
            var identity = new ClaimsIdentity(claims, "ApiToken");
            context.User = new ClaimsPrincipal(identity);
            ApiTokenAuthenticationFeature.MarkAuthenticated(context, ApiTokenScopes.Parse(token.Scopes));
        }

        await _next(context);
    }
}
