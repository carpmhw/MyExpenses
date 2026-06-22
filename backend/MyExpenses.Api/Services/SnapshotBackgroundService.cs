using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

public class SnapshotBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SnapshotBackgroundService> _logger;

    public SnapshotBackgroundService(IServiceScopeFactory scopeFactory, ILogger<SnapshotBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Snapshot background service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndRunSnapshot(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking snapshot schedule");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task CheckAndRunSnapshot(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var config = await db.AutoSnapshotConfigs.FirstOrDefaultAsync(ct);
        if (config is null || !config.IsEnabled) return;

        var now = DateTime.UtcNow;
        var today = now.Date;

        if (config.LastRunAt.HasValue && config.LastRunAt.Value.Date == today)
            return;

        var timeParts = config.TimeOfDay.Split(':');
        if (!int.TryParse(timeParts[0], out var hour) || !int.TryParse(timeParts[1], out var minute))
            return;

        var scheduledTime = today.AddHours(hour).AddMinutes(minute);
        if (now < scheduledTime) return;

        var shouldRun = config.Frequency switch
        {
            "Daily" => true,
            "Weekly" => config.DayOfWeek.HasValue && (int)now.DayOfWeek == config.DayOfWeek.Value,
            "Monthly" => config.DayOfMonth.HasValue && now.Day == config.DayOfMonth.Value,
            _ => false,
        };

        if (!shouldRun) return;

        var bankAccounts = await db.BankAccounts.ToListAsync(ct);
        var stocks = await db.Stocks.ToListAsync(ct);

        var bankDetails = bankAccounts.Select(b => new BankDetail
        {
            BankName = b.BankName,
            AccountNumber = b.AccountNumber,
            AccountType = b.AccountType,
            Balance = b.Balance,
        }).ToList();

        var totalBankBalance = bankDetails.Sum(b => b.Balance);

        var stockDetails = stocks.Select(s => new StockDetail
        {
            Name = s.Name,
            Symbol = s.Symbol,
            Shares = s.Shares,
            BuyPrice = s.BuyPrice,
            CurrentPrice = s.CurrentPrice,
            MarketValue = s.CurrentPrice * s.Shares,
            GainLoss = (s.CurrentPrice - s.BuyPrice) * s.Shares,
        }).ToList();

        var totalStockValue = stockDetails.Sum(s => s.MarketValue);
        var totalStockCost = stockDetails.Sum(s => s.BuyPrice * s.Shares);
        var totalNetWorth = totalBankBalance + totalStockValue;

        var snapshot = new SnapshotBatch
        {
            Name = $"自動快照 {now:yyyy-MM-dd HH:mm}",
            SnapshotDate = now,
            Notes = "系統自動建立",
            TotalNetWorth = totalNetWorth,
            TotalBankBalance = totalBankBalance,
            TotalStockValue = totalStockValue,
            TotalStockCost = totalStockCost,
            BankDetails = bankDetails,
            StockDetails = stockDetails,
        };

        db.SnapshotBatches.Add(snapshot);
        config.LastRunAt = now;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Auto snapshot created: {Id} at {Date}", snapshot.Id, now);
    }
}
