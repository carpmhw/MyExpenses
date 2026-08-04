namespace MyExpenses.Api.Options;

public sealed class BootstrapOptions
{
    public const string SectionName = "Bootstrap";

    public string? Secret { get; set; }
}
