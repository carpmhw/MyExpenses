using Microsoft.AspNetCore.Http;

namespace MyExpenses.Api.Services;

/// <summary>在 Remote mode response 加入不依賴 edge 實作的 baseline browser headers。</summary>
public sealed class RemoteSecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>建立 Remote browser security headers middleware。</summary>
    public RemoteSecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    /// <summary>加入禁止 framing、MIME sniffing 與 referrer 洩漏的 response headers。</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        await _next(context);
    }
}
