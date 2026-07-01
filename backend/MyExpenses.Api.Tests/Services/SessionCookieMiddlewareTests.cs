using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class SessionCookieMiddlewareTests
{
    /// <summary>Verifies a forged JWT tokenType claim cannot bypass the browser session cookie.</summary>
    [Fact]
    public async Task InvokeAsync_RejectsJwtTokenTypeApiClaimWithoutApiTokenMarker()
    {
        var nextCalled = false;
        var middleware = new SessionCookieMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, NullLogger<SessionCookieMiddleware>.Instance);
        var context = CreateHttpContextWithUser(new Claim("tokenType", "api"));

        await middleware.InvokeAsync(context, new EphemeralDataProtectionProvider());

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    /// <summary>Verifies a verified API token marker bypasses browser session cookie validation.</summary>
    [Fact]
    public async Task InvokeAsync_AllowsApiTokenMarkerWithoutSessionCookie()
    {
        var nextCalled = false;
        var middleware = new SessionCookieMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, NullLogger<SessionCookieMiddleware>.Instance);
        var context = CreateHttpContextWithUser();
        ApiTokenAuthenticationFeature.MarkAuthenticated(context, Array.Empty<string>());

        await middleware.InvokeAsync(context, new EphemeralDataProtectionProvider());

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>Creates an HTTP context with an authenticated user and optional extra claims.</summary>
    private static DefaultHttpContext CreateHttpContextWithUser(params Claim[] extraClaims)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "1"),
            new("jwtExp", "1234567890"),
        };
        claims.AddRange(extraClaims);

        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")),
        };
        context.Response.Body = new MemoryStream();
        return context;
    }
}
