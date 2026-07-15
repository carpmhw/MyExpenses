namespace MyExpenses.Api.Models.Requests;

/// <summary>Contains a requested IANA system time zone identifier.</summary>
public class UpdateTimeZoneRequest
{
    /// <summary>Gets or sets the requested IANA time zone identifier.</summary>
    public string? TimeZoneId { get; set; }
}
