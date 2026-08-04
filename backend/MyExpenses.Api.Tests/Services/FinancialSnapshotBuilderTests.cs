using MyExpenses.Api.Models;
using MyExpenses.Api.Services;
using Xunit;

namespace MyExpenses.Api.Tests.Services;

public class FinancialSnapshotBuilderTests
{
    /// <summary>Verifies snapshot builder subtracts captured unpaid liabilities from total assets.</summary>
    [Fact]
    public void Build_StoresCompleteAssetAndLiabilityBasis()
    {
        var snapshot = FinancialSnapshotBuilder.Build(
            "test",
            null,
            new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc),
            new[]
            {
                new BankAccount { BankName = "測試銀行", AccountNumber = "12345", AccountType = "活期", Balance = 1000m },
            },
            Array.Empty<Stock>(),
            250m);

        Assert.Equal(1000m, snapshot.TotalAssets);
        Assert.Equal(250m, snapshot.TotalLiabilities);
        Assert.Equal(750m, snapshot.TotalNetWorth);
        Assert.Equal(NetWorthBasis.AssetsMinusLiabilities, snapshot.NetWorthBasis);
    }
}
