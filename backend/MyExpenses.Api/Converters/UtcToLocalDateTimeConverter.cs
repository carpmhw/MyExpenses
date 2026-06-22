using System.Text.Json;
using System.Text.Json.Serialization;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Converters;

/// <summary>
/// 將 UTC DateTime 在 JSON 序列化時自動轉為設定時區的當地時間，並輸出含 UTC 偏移的格式
/// </summary>
public class UtcToLocalDateTimeConverter : JsonConverter<DateTime>
{
    private readonly TimeZoneService _timeZoneService;

    /// <summary>
    /// 初始化轉換器
    /// </summary>
    public UtcToLocalDateTimeConverter(TimeZoneService timeZoneService)
    {
        _timeZoneService = timeZoneService;
    }

    /// <summary>讀取 JSON 字串為 DateTime，確保一律轉為 UTC</summary>
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateTime.Parse(reader.GetString() ?? string.Empty).ToUniversalTime();

    /// <summary>將 DateTime 寫為 JSON 字串（UTC 轉當地時間 + 偏移）</summary>
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var localValue = _timeZoneService.ConvertUtcToLocal(value);
        // 輸出含偏移的格式，如 "2026-06-20T22:30:00+08:00"
        writer.WriteStringValue(localValue.ToString("yyyy-MM-ddTHH:mm:ssK"));
    }
}
