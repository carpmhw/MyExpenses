using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace MyExpenses.Api.Services;

public class SessionCookieMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SessionCookieMiddleware> _logger;

    public SessionCookieMiddleware(RequestDelegate next, ILogger<SessionCookieMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IDataProtectionProvider dataProtection)
    {
        if (ApiTokenAuthenticationFeature.IsAuthenticated(context))
        {
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (!context.Request.Cookies.TryGetValue("mx_session", out var cookieValue))
            {
                _logger.LogWarning("Session cookie missing for user {UserId}",
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier));
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Session cookie missing");
                return;
            }

            try
            {
                var protector = dataProtection.CreateProtector("MyExpenses.Session");
                var decrypted = Encoding.UTF8.GetString(protector.Unprotect(
                    Convert.FromBase64String(cookieValue)));

                var parts = decrypted.Split(':');
                if (parts.Length != 2)
                {
                    throw new InvalidOperationException("Invalid cookie format");
                }

                var cookieUserId = parts[0];
                var cookieJwtExp = parts[1];

                var jwtUserId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
                var jwtJwtExp = context.User.FindFirstValue("jwtExp");

                _logger.LogDebug("Session cookie decrypted: cookie({UserId}:{Exp}) jwt({JwtUserId}:{JwtExp})",
                    cookieUserId, cookieJwtExp, jwtUserId, jwtJwtExp);

                if (cookieUserId != jwtUserId || cookieJwtExp != jwtJwtExp)
                {
                    throw new InvalidOperationException(
                        $"Mismatch: cookie({cookieUserId}:{cookieJwtExp}) jwt({jwtUserId}:{jwtJwtExp})");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session cookie validation failed");
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync($"Session cookie invalid: {ex.GetType().Name}: {ex.Message}");
                return;
            }
        }

        await _next(context);
    }
}
