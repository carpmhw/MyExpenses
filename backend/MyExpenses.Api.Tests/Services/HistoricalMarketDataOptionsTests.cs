using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class HistoricalMarketDataOptionsTests
{
    /// <summary>驗證歷史行情預設保留 60 個日曆月且仍保留 bounded response 上限。</summary>
    [Fact]
    public void Defaults_UseSixtyMonthHistoryAndBoundedResponse()
    {
        var options = new HistoricalMarketDataOptions();

        Assert.Equal(60, options.HistoryMonths);
        Assert.True(options.MaxResponseBytes > 0);
    }

    /// <summary>驗證歷史期間只接受安全的 1 到 60 個月範圍。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void HistoryMonths_RejectsOutOfRangeValues(int value)
    {
        var options = new HistoricalMarketDataOptions { HistoryMonths = value };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }
}
