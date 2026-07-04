using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class ApiTokenScopesTests
{
    /// <summary>Verifies null and blank scope payloads fail closed to an empty scope set.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_ReturnsEmptySetWhenSerializedScopesAreMissing(string? serializedScopes)
    {
        var scopes = ApiTokenScopes.Parse(serializedScopes);

        Assert.Empty(scopes);
    }

    /// <summary>Verifies invalid JSON fail closed to an empty scope set.</summary>
    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"scope\":\"transactions:read\"}")]
    [InlineData("[1,2,3]")]
    public void Parse_ReturnsEmptySetWhenSerializedScopesAreInvalid(string serializedScopes)
    {
        var scopes = ApiTokenScopes.Parse(serializedScopes);

        Assert.Empty(scopes);
    }

    /// <summary>Verifies valid scopes are normalized by removing blanks and duplicates.</summary>
    [Fact]
    public void Parse_ReturnsDistinctNonBlankScopes()
    {
        var scopes = ApiTokenScopes.Parse("[\"transactions:read\",\"\",\"transactions:read\",\"reports:read\"]");

        Assert.Equal(new[] { "reports:read", "transactions:read" }, scopes.OrderBy(s => s));
    }
}
