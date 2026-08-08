using System.Globalization;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>保存自動快照設定驗證結果與安全欄位錯誤。</summary>
public sealed record AutoSnapshotScheduleValidationResult(
    bool IsValid,
    IReadOnlyDictionary<string, string[]> Errors);

/// <summary>驗證自動快照 frequency、時間與日曆欄位契約。</summary>
public static class AutoSnapshotScheduleValidator
{
    /// <summary>回傳不修改既有設定的自動快照設定驗證結果。</summary>
    public static AutoSnapshotScheduleValidationResult Validate(AutoSnapshotConfig input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (input.Frequency is not ("Daily" or "Weekly" or "Monthly"))
            AddError(errors, nameof(input.Frequency), "Frequency 必須是 Daily、Weekly 或 Monthly。");

        if (!TimeOnly.TryParseExact(
                input.TimeOfDay,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
            AddError(errors, nameof(input.TimeOfDay), "TimeOfDay 必須符合 HH:mm。");

        if (input.DayOfWeek is < 0 or > 6)
            AddError(errors, nameof(input.DayOfWeek), "DayOfWeek 必須介於 0 至 6。");
        if (input.DayOfMonth is < 1 or > 31)
            AddError(errors, nameof(input.DayOfMonth), "DayOfMonth 必須介於 1 至 31。");
        if (input.Frequency == "Weekly" && !input.DayOfWeek.HasValue)
            AddError(errors, nameof(input.DayOfWeek), "Weekly 必須提供 DayOfWeek。");
        if (input.Frequency == "Monthly" && !input.DayOfMonth.HasValue)
            AddError(errors, nameof(input.DayOfMonth), "Monthly 必須提供 DayOfMonth。");

        return new AutoSnapshotScheduleValidationResult(
            errors.Count == 0,
            errors.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>加入指定欄位的 bounded validation error。</summary>
    private static void AddError(
        IDictionary<string, List<string>> errors,
        string field,
        string message)
    {
        if (!errors.TryGetValue(field, out var messages))
        {
            messages = [];
            errors[field] = messages;
        }

        messages.Add(message);
    }
}
