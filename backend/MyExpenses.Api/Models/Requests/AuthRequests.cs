namespace MyExpenses.Api.Models.Requests;

public class RegisterRequest
{
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class TwoFactorLoginRequest
{
    public string TempToken { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

public class UpdateProfileRequest
{
    public string DisplayName { get; set; } = string.Empty;
}

public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public class VerifyTwoFactorRequest
{
    public string Code { get; set; } = string.Empty;
}

public class RecoveryCodeLoginRequest
{
    public string TempToken { get; set; } = string.Empty;
    public string RecoveryCode { get; set; } = string.Empty;
}
