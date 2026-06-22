namespace MyExpenses.Api.Models.Requests;

public class CreateApiTokenRequest
{
    public string Name { get; set; } = string.Empty;
    public string[]? Scopes { get; set; }
}
