namespace MyExpenses.Api.Services;

/// <summary>Represents a half-open UTC interval for instant filtering.</summary>
public sealed record UtcDateTimeRange(DateTime StartUtc, DateTime EndExclusiveUtc);
