namespace MyExpenses.Api.Services;

public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    /// <summary>Returns a validation error when a password does not meet the shared password policy.</summary>
    public static string? Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return "Password is required.";
        }

        if (password.Length < MinimumLength)
        {
            return $"Password must be at least {MinimumLength} characters and include uppercase, lowercase, number, and symbol.";
        }

        var hasUppercase = password.Any(char.IsUpper);
        var hasLowercase = password.Any(char.IsLower);
        var hasNumber = password.Any(char.IsDigit);
        var hasSymbol = password.Any(c => !char.IsLetterOrDigit(c));

        if (!hasUppercase || !hasLowercase || !hasNumber || !hasSymbol)
        {
            return "Password must include uppercase, lowercase, number, and symbol.";
        }

        return null;
    }
}
