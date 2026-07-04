using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class ApiTokenScopeMiddlewareTests
{
    /// <summary>Verifies browser JWT requests are ignored by API token scope enforcement.</summary>
    [Fact]
    public async Task InvokeAsync_AllowsNonApiTokenRequests()
    {
        var nextCalled = false;
        var middleware = new ApiTokenScopeMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        SetEndpoint(context, new ApiTokenScopeMetadata(ApiTokenScopes.TransactionsRead));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>Verifies API token requests with the required scope are allowed.</summary>
    [Fact]
    public async Task InvokeAsync_AllowsApiTokenWithRequiredScope()
    {
        var nextCalled = false;
        var middleware = new ApiTokenScopeMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateApiTokenContext(ApiTokenScopes.TransactionsRead);
        SetEndpoint(context, new ApiTokenScopeMetadata(ApiTokenScopes.TransactionsRead));

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>Verifies API token requests without the required scope are rejected.</summary>
    [Fact]
    public async Task InvokeAsync_RejectsApiTokenWithoutRequiredScope()
    {
        var nextCalled = false;
        var middleware = new ApiTokenScopeMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateApiTokenContext(ApiTokenScopes.TransactionsRead);
        SetEndpoint(context, new ApiTokenScopeMetadata(ApiTokenScopes.TransactionsWrite));

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    /// <summary>Verifies API token requests to unmarked endpoints are rejected by default.</summary>
    [Fact]
    public async Task InvokeAsync_RejectsApiTokenWhenEndpointHasNoScopeMetadata()
    {
        var nextCalled = false;
        var middleware = new ApiTokenScopeMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateApiTokenContext(ApiTokenScopes.TransactionsRead);
        SetEndpoint(context);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    /// <summary>Verifies anonymous public endpoints are not changed by API token scope enforcement.</summary>
    [Fact]
    public async Task InvokeAsync_AllowsAnonymousEndpointWithoutScopeMetadata()
    {
        var nextCalled = false;
        var middleware = new ApiTokenScopeMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = CreateApiTokenContext(ApiTokenScopes.TransactionsRead);
        SetEndpoint(context, new AllowAnonymousAttribute());

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>Creates a request marked as authenticated by an API token with the provided scopes.</summary>
    private static DefaultHttpContext CreateApiTokenContext(params string[] scopes)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        ApiTokenAuthenticationFeature.MarkAuthenticated(context, scopes);
        return context;
    }

    /// <summary>Attaches a selected endpoint with the supplied metadata to the test context.</summary>
    private static void SetEndpoint(DefaultHttpContext context, params object[] metadata)
    {
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test"));
    }
}
