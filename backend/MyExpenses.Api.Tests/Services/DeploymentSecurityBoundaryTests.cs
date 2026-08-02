using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyExpenses.Api.Options;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class DeploymentSecurityBoundaryTests
{
    /// <summary>驗證 Development CORS 僅允許明確設定的 frontend origin。</summary>
    [Fact]
    public async Task DevelopmentCors_AllowsConfiguredOriginOnly()
    {
        await using var app = await CreateCorsAppAsync("http://localhost:5173");
        var client = app.GetTestClient();

        using var allowedRequest = CreateOriginRequest("http://localhost:5173");
        using var allowedResponse = await client.SendAsync(allowedRequest);

        Assert.Equal(HttpStatusCode.OK, allowedResponse.StatusCode);
        Assert.Equal(
            "http://localhost:5173",
            allowedResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var rejectedRequest = CreateOriginRequest("https://attacker.example");
        using var rejectedResponse = await client.SendAsync(rejectedRequest);

        Assert.Equal(HttpStatusCode.OK, rejectedResponse.StatusCode);
        Assert.False(rejectedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    /// <summary>驗證 trusted proxy 會提供 forwarded scheme 與 client IP 給應用程式。</summary>
    [Fact]
    public async Task ForwardedHeaders_UseConfiguredProxyValues()
    {
        await using var app = await CreateForwardedHeadersAppAsync(
            new DeploymentOptions { TrustedProxies = ["127.0.0.1"] });
        var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/identity");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.42");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("scheme=https", body, StringComparison.Ordinal);
        Assert.Contains("ip=203.0.113.42", body, StringComparison.Ordinal);
    }

    /// <summary>驗證未列入 allowlist 的來源不能 spoof forwarded scheme 或 client IP。</summary>
    [Fact]
    public async Task ForwardedHeaders_IgnoreSpoofedValuesFromUntrustedSource()
    {
        await using var app = await CreateForwardedHeadersAppAsync(
            new DeploymentOptions { TrustedProxies = ["127.0.0.1"] },
            forcedRemoteAddress: "198.51.100.20");
        var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/identity");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.42");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("scheme=http", body, StringComparison.Ordinal);
        Assert.Contains("ip=198.51.100.20", body, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.42", body, StringComparison.Ordinal);
    }

    /// <summary>驗證 forwarded headers 在 rate limiter 前套用，避免所有 proxy client 共用同一個 bucket。</summary>
    [Fact]
    public async Task ForwardedHeaders_RunBeforeRateLimiter()
    {
        await using var app = await CreateRateLimitedForwardedHeadersAppAsync();
        var client = app.GetTestClient();

        for (var index = 0; index <= AuthRateLimitPolicy.PermitLimit; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/limited");
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "http");
            request.Headers.TryAddWithoutValidation(
                "X-Forwarded-For",
                $"203.0.113.{index + 1}");

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>驗證 Remote response 具備 HSTS 與 baseline browser security headers。</summary>
    [Fact]
    public async Task RemoteSecurity_AddsHstsAndBrowserSecurityHeaders()
    {
        await using var app = await CreateRemoteSecurityAppAsync();
        var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://expenses.example.com/");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.42");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("max-age=", response.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.Equal(
            "frame-ancestors 'none'",
            response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    /// <summary>建立只註冊 Development CORS 的 test host。</summary>
    private static async Task<WebApplication> CreateCorsAppAsync(string origin)
    {
        var builder = CreateBuilder(Environments.Development);
        builder.Services.AddDevelopmentCors(origin);

        var app = builder.Build();
        app.UseCors();
        app.MapGet("/", () => Results.Ok());
        await app.StartAsync();
        return app;
    }

    /// <summary>建立可觀察 forwarded request scheme 與 remote IP 的 test host。</summary>
    private static async Task<WebApplication> CreateForwardedHeadersAppAsync(
        DeploymentOptions options,
        string? forcedRemoteAddress = null)
    {
        var builder = CreateBuilder(Environments.Production);
        builder.Services.AddTrustedForwardedHeaders(options);

        var app = builder.Build();
        if (forcedRemoteAddress is not null)
        {
            app.Use(async (context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(forcedRemoteAddress);
                await next(context);
            });
        }

        app.UseForwardedHeaders();
        app.MapGet("/identity", (HttpContext context) =>
            $"scheme={context.Request.Scheme};ip={context.Connection.RemoteIpAddress}");
        await app.StartAsync();
        return app;
    }

    /// <summary>建立 forwarded headers 位於 rate limiter 前的整合 test host。</summary>
    private static async Task<WebApplication> CreateRateLimitedForwardedHeadersAppAsync()
    {
        var builder = CreateBuilder(Environments.Production);
        builder.Services.AddTrustedForwardedHeaders(
            new DeploymentOptions { TrustedProxies = ["127.0.0.1"] });
        builder.Services.AddRateLimiter(AuthRateLimitPolicy.Configure);

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(context);
        });
        app.UseForwardedHeaders();
        app.UseRateLimiter();
        app.MapGet("/limited", () => Results.Ok())
            .RequireRateLimiting(AuthRateLimitPolicy.SensitiveAuthPolicy);
        await app.StartAsync();
        return app;
    }

    /// <summary>建立 Remote security middleware 與 HSTS 的整合 test host。</summary>
    private static async Task<WebApplication> CreateRemoteSecurityAppAsync()
    {
        var builder = CreateBuilder(Environments.Production);
        var options = new DeploymentOptions
        {
            Mode = DeploymentMode.Remote,
            PublicOrigin = "https://expenses.example.com",
            SecureCookies = true,
            TrustedProxies = ["127.0.0.1"],
        };
        builder.Services.AddTrustedForwardedHeaders(options);
        builder.Services.AddDeploymentSecurity(options);

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(context);
        });
        app.UseForwardedHeaders();
        app.UseDeploymentSecurity(options);
        app.MapGet("/", () => Results.Ok());
        await app.StartAsync();
        return app;
    }

    /// <summary>建立共用的 TestServer WebApplication builder。</summary>
    private static WebApplicationBuilder CreateBuilder(string environmentName)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName,
        });
        builder.WebHost.UseTestServer();
        return builder;
    }

    /// <summary>建立帶有指定 Origin header 的 GET request。</summary>
    private static HttpRequestMessage CreateOriginRequest(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Origin", origin);
        return request;
    }
}
