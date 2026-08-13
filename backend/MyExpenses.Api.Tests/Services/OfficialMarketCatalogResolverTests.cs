using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public sealed class OfficialMarketCatalogResolverTests
{
    /// <summary>驗證只存在 TWSE 清單的代號會解析為上市市場。</summary>
    [Fact]
    public void Resolve_ReturnsTwseWhenSymbolOnlyExistsInTwseCatalog()
    {
        var snapshot = CreateSnapshot(
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m, "台積電")]),
            CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m, "環球晶")]));

        var result = OfficialMarketCatalogResolver.Resolve(snapshot, " 2330 ");

        Assert.Equal(StockMarket.Twse, result.Market);
        Assert.Equal("Completed", result.Code);
        Assert.Equal("台積電", result.Record?.Name);
    }

    /// <summary>驗證只存在 TPEx 清單的代號會解析為上櫃市場。</summary>
    [Fact]
    public void Resolve_ReturnsTpexWhenSymbolOnlyExistsInTpexCatalog()
    {
        var snapshot = CreateSnapshot(
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
            CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m, "環球晶")]));

        var result = OfficialMarketCatalogResolver.Resolve(snapshot, "6488");

        Assert.Equal(StockMarket.Tpex, result.Market);
        Assert.Equal("Completed", result.Code);
        Assert.Equal(88m, result.Record?.Price);
    }

    /// <summary>驗證同一代號同時存在兩個市場時不會猜測市場。</summary>
    [Fact]
    public void Resolve_ReturnsAmbiguousWhenSymbolExistsInBothCatalogs()
    {
        var snapshot = CreateSnapshot(
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
            CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("2330", 101m)]));

        var result = OfficialMarketCatalogResolver.Resolve(snapshot, "2330");

        Assert.Equal(StockMarket.Unknown, result.Market);
        Assert.Equal("AmbiguousMarket", result.Code);
        Assert.False(result.Retryable);
    }

    /// <summary>驗證兩個完整市場清單都沒有代號時會回傳找不到。</summary>
    [Fact]
    public void Resolve_ReturnsNotFoundWhenSymbolExistsInNeitherCatalog()
    {
        var snapshot = CreateSnapshot(
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
            CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m)]));

        var result = OfficialMarketCatalogResolver.Resolve(snapshot, "9999");

        Assert.Equal(StockMarket.Unknown, result.Market);
        Assert.Equal("MarketNotFound", result.Code);
        Assert.False(result.Retryable);
    }

    /// <summary>驗證任一官方來源失敗時不會依單邊資料推測市場。</summary>
    [Fact]
    public void Resolve_ReturnsUnavailableWhenOneCatalogFails()
    {
        var snapshot = CreateSnapshot(
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", 100m)]),
            CurrentPriceProviderResult.Failed("TPEx", "ProviderUnavailable", "暫時無法使用", true));

        var result = OfficialMarketCatalogResolver.Resolve(snapshot, "2330");

        Assert.Equal(StockMarket.Unknown, result.Market);
        Assert.Equal("MarketDetectionUnavailable", result.Code);
        Assert.True(result.Retryable);
    }

    /// <summary>驗證永久與暫時來源混合失敗時整體不可重試。</summary>
    [Fact]
    public void Resolve_ReturnsPermanentUnavailableWhenCatalogFailuresAreMixed()
    {
        var snapshot = CreateSnapshot(
            CurrentPriceProviderResult.Failed("TWSE", "ProviderRejected", "永久拒絕", false),
            CurrentPriceProviderResult.Failed("TPEx", "ProviderUnavailable", "暫時無法使用", true));

        var result = OfficialMarketCatalogResolver.Resolve(snapshot, "2330");

        Assert.Equal(StockMarket.Unknown, result.Market);
        Assert.Equal("MarketDetectionUnavailable", result.Code);
        Assert.False(result.Retryable);
    }

    /// <summary>驗證雙來源皆暫時失敗時整體仍可重試。</summary>
    [Fact]
    public void Resolve_ReturnsRetryableUnavailableWhenBothCatalogFailuresAreTransient()
    {
        var snapshot = CreateSnapshot(
            CurrentPriceProviderResult.Failed("TWSE", "ProviderUnavailable", "暫時無法使用", true),
            CurrentPriceProviderResult.Failed("TPEx", "ProviderUnavailable", "暫時無法使用", true));

        var result = OfficialMarketCatalogResolver.Resolve(snapshot, "2330");

        Assert.Equal("MarketDetectionUnavailable", result.Code);
        Assert.True(result.Retryable);
    }

    /// <summary>驗證雙來源皆永久失敗時整體不可重試。</summary>
    [Fact]
    public void Resolve_ReturnsPermanentUnavailableWhenBothCatalogFailuresArePermanent()
    {
        var snapshot = CreateSnapshot(
            CurrentPriceProviderResult.Failed("TWSE", "ProviderRejected", "永久拒絕", false),
            CurrentPriceProviderResult.Failed("TPEx", "ProviderFailure", "永久失敗", false));

        var result = OfficialMarketCatalogResolver.Resolve(snapshot, "2330");

        Assert.Equal("MarketDetectionUnavailable", result.Code);
        Assert.False(result.Retryable);
    }

    /// <summary>驗證市場 membership 與目前價格有效性是彼此獨立的判定。</summary>
    [Fact]
    public void ResolveKeepsMarketWhenCatalogRecordHasInvalidPrice()
    {
        var snapshot = CreateSnapshot(
            CurrentPriceProviderResult.Success("TWSE", [new CurrentPriceRecord("2330", null, "台積電")]),
            CurrentPriceProviderResult.Success("TPEx", [new CurrentPriceRecord("6488", 88m)]));

        var result = OfficialMarketCatalogResolver.Resolve(snapshot, "2330");

        Assert.Equal(StockMarket.Twse, result.Market);
        Assert.Equal("Completed", result.Code);
        Assert.Null(result.Record?.Price);
    }

    /// <summary>驗證受控官方 fixture 中上市與上櫃代表代號的市場判定及來源失敗保護。</summary>
    [Fact]
    public void Resolve_UsesControlledRepresentativeSymbols()
    {
        var snapshot = CreateSnapshot(
            CurrentPriceProviderResult.Success("TWSE", [
                new CurrentPriceRecord("2330", 100m),
                new CurrentPriceRecord("0050", 200m),
            ]),
            CurrentPriceProviderResult.Success("TPEx", [
                new CurrentPriceRecord("6488", 88m),
                new CurrentPriceRecord("00679B", 50m),
            ]));

        Assert.Equal(StockMarket.Twse, OfficialMarketCatalogResolver.Resolve(snapshot, "2330").Market);
        Assert.Equal(StockMarket.Twse, OfficialMarketCatalogResolver.Resolve(snapshot, "0050").Market);
        Assert.Equal(StockMarket.Tpex, OfficialMarketCatalogResolver.Resolve(snapshot, "6488").Market);
        Assert.Equal(StockMarket.Tpex, OfficialMarketCatalogResolver.Resolve(snapshot, "00679B").Market);

        var unavailable = new OfficialMarketCatalogSnapshot(
            snapshot.Twse,
            CurrentPriceProviderResult.Failed("TPEx", "ProviderUnavailable", "暫時無法使用", true));
        var result = OfficialMarketCatalogResolver.Resolve(unavailable, "2330");
        Assert.Equal(StockMarket.Unknown, result.Market);
        Assert.Equal("MarketDetectionUnavailable", result.Code);
    }

    /// <summary>建立官方雙市場測試快照。</summary>
    private static OfficialMarketCatalogSnapshot CreateSnapshot(
        CurrentPriceProviderResult twse,
        CurrentPriceProviderResult tpex)
        => new(twse, tpex);
}
