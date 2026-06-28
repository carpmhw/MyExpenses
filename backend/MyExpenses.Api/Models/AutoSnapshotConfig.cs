namespace MyExpenses.Api.Models;

public class AutoSnapshotConfig
{
    public int Id { get; set; }
    public bool IsEnabled { get; set; }
    public string Frequency { get; set; } = "Daily";
    public int? DayOfWeek { get; set; }
    public int? DayOfMonth { get; set; }
    public string TimeOfDay { get; set; } = "08:00";
    public DateTime? LastRunAt { get; set; }
}
