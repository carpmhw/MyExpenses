using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace MyExpenses.Api.Services;

public class SessionCookieMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SessionCookieMiddleware> _logger;

    /// <summary>建立使用指定 request delegate 與 logger 的 session cookie middleware。</summary>
    public SessionCookieMiddleware(RequestDelegate next, ILogger<SessionCookieMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>驗證 browser session cookie，但不把 cookie 明文或解密內容寫入 log。</summary>
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

                if (cookieUserId != jwtUserId || cookieJwtExp != jwtJwtExp)
                {
                    throw new InvalidOperationException(
                        $"Mismatch: cookie({cookieUserId}:{cookieJwtExp}) jwt({jwtUserId}:{jwtJwtExp})");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Session cookie validation failed for user {UserId}; reason type {FailureType}",
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                    ex.GetType().Name);
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Session cookie invalid");
                return;
            }
        }

        await _next(context);
    }
}
