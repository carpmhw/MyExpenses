using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class PasswordPolicyTests
{
    /// <summary>Verifies strong passwords are accepted by the shared password policy.</summary>
    [Fact]
    public void Validate_ReturnsNullForStrongPassword()
    {
        var error = PasswordPolicy.Validate("Valid!Password123");

        Assert.Null(error);
    }

    /// <summary>Verifies short passwords are rejected by the shared password policy.</summary>
    [Fact]
    public void Validate_RejectsShortPassword()
    {
        var error = PasswordPolicy.Validate("Short1!");

        Assert.Contains("at least 12", error);
    }

    /// <summary>Verifies passwords missing required character classes are rejected.</summary>
    [Theory]
    [InlineData("valid!password123")]
    [InlineData("VALID!PASSWORD123")]
    [InlineData("Valid!PasswordOnly")]
    [InlineData("ValidPassword123")]
    public void Validate_RejectsPasswordMissingRequiredCharacterClass(string password)
    {
        var error = PasswordPolicy.Validate(password);

        Assert.Contains("uppercase, lowercase, number, and symbol", error);
    }
}
