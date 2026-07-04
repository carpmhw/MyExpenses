using Microsoft.AspNetCore.Http;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class ApiTokenAuthenticationFeatureTests
{
    /// <summary>Verifies requests are not treated as API-token authenticated by default.</summary>
    [Fact]
    public void IsAuthenticated_ReturnsFalseByDefault()
    {
        var context = new DefaultHttpContext();

        var authenticated = ApiTokenAuthenticationFeature.IsAuthenticated(context);

        Assert.False(authenticated);
    }

    /// <summary>Verifies marking a request stores API token scopes for downstream middleware.</summary>
    [Fact]
    public void MarkAuthenticated_AllowsMiddlewareToDetectApiTokenScopes()
    {
        var context = new DefaultHttpContext();

        ApiTokenAuthenticationFeature.MarkAuthenticated(context, new[] { ApiTokenScopes.TransactionsRead });
        var authenticated = ApiTokenAuthenticationFeature.IsAuthenticated(context);

        Assert.True(authenticated);
        Assert.True(ApiTokenAuthenticationFeature.HasScope(context, ApiTokenScopes.TransactionsRead));
        Assert.False(ApiTokenAuthenticationFeature.HasScope(context, ApiTokenScopes.TransactionsWrite));
    }
}
