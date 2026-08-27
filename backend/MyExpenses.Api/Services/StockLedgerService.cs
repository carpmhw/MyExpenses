using Microsoft.EntityFrameworkCore;
using MyExpenses.Api.Data;
using MyExpenses.Api.Models;

namespace MyExpenses.Api.Services;

/// <summary>描述股票 Ledger mutation 的原始交易命令。</summary>
public sealed record StockLedgerTransactionCommand(
    StockTransactionType Type,
    DateOnly TradeDate,
    decimal? Shares = null,
    decimal? Price = null,
    decimal Fee = 0m,
    decimal Tax = 0m,
    decimal? CashAmount = null,
    decimal? OpeningMarketValue = null,
    string? Notes = null);

/// <summary>描述 mutation commit 後的交易與完整 replay 結果。</summary>
public sealed record StockLedgerMutationResult(
    StockTransaction Transaction,
    StockLedgerResult Replay,
    StockLedgerEntryResult EntryResult);

/// <summary>描述既有持股初始化命令。</summary>
public sealed record StockLedgerInitializationCommand(DateOnly BaselineDate);

/// <summary>描述無法安全建立 synthetic opening 的股票與穩定原因。</summary>
public sealed record StockLedgerBlockingStock(
    int StockId,
    string Symbol,
    string Reason,
    decimal BuyPrice,
    decimal CurrentPrice)
{
    public string Code => Reason;
}

/// <summary>描述整批 Ledger 初始化的原子結果。</summary>
public sealed record StockLedgerInitializationResponse(
    int InitializedCount,
    int SkippedCount,
    int BlockingCount,
    IReadOnlyList<StockLedgerBlockingStock> BlockingStocks)
{
    public int TotalCount => InitializedCount + SkippedCount + BlockingCount;
}

/// <summary>描述新股票與第一筆 Ledger 的 atomic position command。</summary>
public sealed record StockPositionCommand(
    string Name,
    string Symbol,
    StockMarket Market,
    StockInstrumentType InstrumentType,
    decimal Shares,
    decimal BuyPrice,
    decimal CurrentPrice,
    DateOnly TradeDate,
    StockTransactionType InitialTransactionType,
    string? Broker = null,
    decimal Fee = 0m,
    decimal Tax = 0m,
    decimal? OpeningMarketValue = null,
    string? Notes = null);

/// <summary>描述 atomic position command 建立的股票、交易與 replay projection。</summary>
public sealed record StockPositionMutationResult(
    Stock Stock,
    StockTransaction Transaction,
    StockLedgerResult Replay);

/// <summary>表示要求的股票或交易不存在。</summary>
public sealed class StockLedgerNotFoundException : KeyNotFoundException
{
    /// <summary>建立安全且不暴露資料庫細節的 not-found 例外。</summary>
    public StockLedgerNotFoundException(string message)
        : base(message)
    {
    }
}

/// <summary>集中執行 Ledger mutation、完整 replay、Stock projection 與初始化 transaction。</summary>
public sealed class StockLedgerService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _timeProvider;

    /// <summary>建立使用指定資料庫 context 與 clock 的 Ledger service。</summary>
    public StockLedgerService(AppDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>建立一筆交易、完整 replay 並原子更新 Stock projection。</summary>
    public async Task<StockLedgerMutationResult> CreateTransactionAsync(
        int stockId,
        StockLedgerTransactionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var stock = await _db.Stocks.SingleOrDefaultAsync(item => item.Id == stockId, cancellationToken)
            ?? throw new StockLedgerNotFoundException("股票不存在");

        if (command.Type == StockTransactionType.StockDividend
            && !await _db.StockTransactions.AnyAsync(item => item.StockId == stockId, cancellationToken))
        {
            throw new StockLedgerException(
                StockLedgerFailureCode.InvalidTransaction,
                "股票股利必須先有 Ledger 初始部位");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var sequence = await _db.StockTransactions
                .Where(item => item.StockId == stockId && item.TradeDate == command.TradeDate)
                .Select(item => (int?)item.Sequence)
                .MaxAsync(cancellationToken) ?? 0;
            var entity = CreateEntity(stockId, command, sequence + 1);
            StockLedgerCalculator.Validate(ToEntry(entity));
            _db.StockTransactions.Add(entity);
            await _db.SaveChangesAsync(cancellationToken);
            var replay = await ReplayAndProjectCoreAsync(stockId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return CreateMutationResult(entity, replay);
        }
        catch
        {
            await RollbackAndClearAsync(transaction, cancellationToken);
            throw;
        }
    }

    /// <summary>提供簡短命名的交易建立別名供 endpoint 與測試使用。</summary>
    public Task<StockLedgerMutationResult> CreateAsync(
        int stockId,
        StockLedgerTransactionCommand command,
        CancellationToken cancellationToken = default)
        => CreateTransactionAsync(stockId, command, cancellationToken);

    /// <summary>修改交易並在同一 transaction 內重播整檔股票歷史。</summary>
    public async Task<StockLedgerMutationResult> UpdateTransactionAsync(
        int transactionId,
        int stockId,
        StockLedgerTransactionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var entity = await _db.StockTransactions
            .SingleOrDefaultAsync(item => item.Id == transactionId && item.StockId == stockId, cancellationToken)
            ?? throw new StockLedgerNotFoundException("股票交易不存在");

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (entity.TradeDate != command.TradeDate)
            {
                entity.Sequence = await _db.StockTransactions
                    .Where(item => item.StockId == stockId
                        && item.TradeDate == command.TradeDate
                        && item.Id != transactionId)
                    .Select(item => (int?)item.Sequence)
                    .MaxAsync(cancellationToken) is int maxSequence
                        ? maxSequence + 1
                        : 1;
            }

            ApplyCommand(entity, command);
            StockLedgerCalculator.Validate(ToEntry(entity));
            await _db.SaveChangesAsync(cancellationToken);
            var replay = await ReplayAndProjectCoreAsync(stockId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return CreateMutationResult(entity, replay);
        }
        catch
        {
            await RollbackAndClearAsync(transaction, cancellationToken);
            throw;
        }
    }

    /// <summary>提供簡短命名的交易修改別名供 endpoint 與測試使用。</summary>
    public Task<StockLedgerMutationResult> UpdateAsync(
        int transactionId,
        int stockId,
        StockLedgerTransactionCommand command,
        CancellationToken cancellationToken = default)
        => UpdateTransactionAsync(transactionId, stockId, command, cancellationToken);

    /// <summary>刪除交易並在同一 transaction 內驗證剩餘歷史仍可 replay。</summary>
    public async Task<StockLedgerResult> DeleteTransactionAsync(
        int transactionId,
        CancellationToken cancellationToken = default)
    {
        var entity = await _db.StockTransactions
            .SingleOrDefaultAsync(item => item.Id == transactionId, cancellationToken)
            ?? throw new StockLedgerNotFoundException("股票交易不存在");
        var stockId = entity.StockId;

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            _db.StockTransactions.Remove(entity);
            await _db.SaveChangesAsync(cancellationToken);
            var replay = await ReplayAndProjectCoreAsync(stockId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }
        catch
        {
            await RollbackAndClearAsync(transaction, cancellationToken);
            throw;
        }
    }

    /// <summary>提供簡短命名的交易刪除別名供 endpoint 與測試使用。</summary>
    public Task<StockLedgerResult> DeleteAsync(
        int transactionId,
        CancellationToken cancellationToken = default)
        => DeleteTransactionAsync(transactionId, cancellationToken);

    /// <summary>在獨立 transaction 內完整 replay 一檔股票並更新相容 projection。</summary>
    public async Task<StockLedgerResult> ReplayAndProjectAsync(
        int stockId,
        CancellationToken cancellationToken = default)
    {
        if (!await _db.Stocks.AnyAsync(item => item.Id == stockId, cancellationToken))
            throw new StockLedgerNotFoundException("股票不存在");

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var replay = await ReplayAndProjectCoreAsync(stockId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }
        catch
        {
            await RollbackAndClearAsync(transaction, cancellationToken);
            throw;
        }
    }

    /// <summary>原子初始化所有尚無 Ledger 且具正股數的既有持股。</summary>
    public async Task<StockLedgerInitializationResponse> InitializeAsync(
        StockLedgerInitializationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var stocks = await _db.Stocks.ToListAsync(cancellationToken);
        var blockingStocks = new List<StockLedgerBlockingStock>();
        var candidates = new List<Stock>();
        var skippedCount = 0;

        foreach (var stock in stocks)
        {
            if (stock.Shares <= 0m || await _db.StockTransactions.AnyAsync(
                    item => item.StockId == stock.Id,
                    cancellationToken))
            {
                skippedCount++;
                continue;
            }

            if (stock.BuyPrice <= 0m || stock.CurrentPrice <= 0m)
            {
                blockingStocks.Add(new StockLedgerBlockingStock(
                    stock.Id,
                    stock.Symbol,
                    stock.BuyPrice <= 0m ? "MissingBuyPrice" : "MissingCurrentPrice",
                    stock.BuyPrice,
                    stock.CurrentPrice));
                continue;
            }

            candidates.Add(stock);
        }

        if (blockingStocks.Count > 0)
        {
            return new StockLedgerInitializationResponse(
                0,
                skippedCount,
                blockingStocks.Count,
                blockingStocks);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var stock in candidates)
            {
                _db.StockTransactions.Add(new StockTransaction
                {
                    StockId = stock.Id,
                    Type = StockTransactionType.OpeningBalance,
                    TradeDate = command.BaselineDate,
                    Sequence = 1,
                    Shares = stock.Shares,
                    Price = stock.BuyPrice,
                    Fee = 0m,
                    Tax = 0m,
                    OpeningMarketValue = stock.Shares * stock.CurrentPrice,
                    CreatedAtUtc = GetUtcNow(),
                    UpdatedAtUtc = GetUtcNow(),
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
            foreach (var stock in candidates)
                await ReplayAndProjectCoreAsync(stock.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new StockLedgerInitializationResponse(
                candidates.Count,
                skippedCount,
                0,
                []);
        }
        catch
        {
            await RollbackAndClearAsync(transaction, cancellationToken);
            throw;
        }
    }

    /// <summary>提供 initialization 命名別名以配合 endpoint contract。</summary>
    public Task<StockLedgerInitializationResponse> InitializeLedgerAsync(
        StockLedgerInitializationCommand command,
        CancellationToken cancellationToken = default)
        => InitializeAsync(command, cancellationToken);

    /// <summary>在單一 transaction 內建立新 Stock 與第一筆 Buy 或 OpeningBalance。</summary>
    public async Task<StockPositionMutationResult> CreatePositionAsync(
        StockPositionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.InitialTransactionType is not (StockTransactionType.Buy or StockTransactionType.OpeningBalance))
            throw new StockLedgerException(
                StockLedgerFailureCode.InvalidTransaction,
                "新部位只能以買入或期初部位建立");

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var stock = new Stock
            {
                Name = command.Name,
                Symbol = command.Symbol,
                Market = command.Market,
                InstrumentType = command.InstrumentType,
                Shares = 0m,
                BuyPrice = 0m,
                CurrentPrice = command.CurrentPrice,
                Broker = command.Broker,
            };
            _db.Stocks.Add(stock);
            await _db.SaveChangesAsync(cancellationToken);

            decimal? openingMarketValue = command.InitialTransactionType == StockTransactionType.OpeningBalance
                ? command.OpeningMarketValue ?? command.Shares * command.CurrentPrice
                : null;
            var entity = new StockTransaction
            {
                StockId = stock.Id,
                Type = command.InitialTransactionType,
                TradeDate = command.TradeDate,
                Sequence = 1,
                Shares = command.Shares,
                Price = command.BuyPrice,
                Fee = command.Fee,
                Tax = command.Tax,
                OpeningMarketValue = openingMarketValue,
                Notes = command.Notes,
                CreatedAtUtc = GetUtcNow(),
                UpdatedAtUtc = GetUtcNow(),
            };
            StockLedgerCalculator.Validate(ToEntry(entity));
            _db.StockTransactions.Add(entity);
            await _db.SaveChangesAsync(cancellationToken);
            var replay = await ReplayAndProjectCoreAsync(stock.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new StockPositionMutationResult(stock, entity, replay);
        }
        catch
        {
            await RollbackAndClearAsync(transaction, cancellationToken);
            throw;
        }
    }

    /// <summary>提供 atomic position 命名別名以配合 endpoint contract。</summary>
    public Task<StockPositionMutationResult> CreateAtomicPositionAsync(
        StockPositionCommand command,
        CancellationToken cancellationToken = default)
        => CreatePositionAsync(command, cancellationToken);

    /// <summary>將原始交易命令轉成 EF entity 並指定同日 replay sequence。</summary>
    private StockTransaction CreateEntity(
        int stockId,
        StockLedgerTransactionCommand command,
        int sequence)
    {
        var now = GetUtcNow();
        return new StockTransaction
        {
            StockId = stockId,
            Type = command.Type,
            TradeDate = command.TradeDate,
            Sequence = sequence,
            Shares = command.Shares,
            Price = command.Price,
            Fee = command.Fee,
            Tax = command.Tax,
            CashAmount = command.CashAmount,
            OpeningMarketValue = command.OpeningMarketValue,
            Notes = command.Notes,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
    }

    /// <summary>將修改命令套用到既有交易並更新 UTC 稽核時間。</summary>
    private void ApplyCommand(StockTransaction entity, StockLedgerTransactionCommand command)
    {
        entity.Type = command.Type;
        entity.TradeDate = command.TradeDate;
        entity.Shares = command.Shares;
        entity.Price = command.Price;
        entity.Fee = command.Fee;
        entity.Tax = command.Tax;
        entity.CashAmount = command.CashAmount;
        entity.OpeningMarketValue = command.OpeningMarketValue;
        entity.Notes = command.Notes;
        entity.UpdatedAtUtc = GetUtcNow();
    }

    /// <summary>載入完整交易歷史、執行純 replay 並更新 Stock 相容欄位。</summary>
    private async Task<StockLedgerResult> ReplayAndProjectCoreAsync(
        int stockId,
        CancellationToken cancellationToken)
    {
        var stock = await _db.Stocks.SingleOrDefaultAsync(item => item.Id == stockId, cancellationToken)
            ?? throw new StockLedgerNotFoundException("股票不存在");
        var transactions = await _db.StockTransactions
            .Where(item => item.StockId == stockId)
            .OrderBy(item => item.TradeDate)
            .ThenBy(item => item.Sequence)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        var replay = StockLedgerCalculator.Replay(transactions);
        stock.Shares = replay.RemainingShares;
        stock.BuyPrice = replay.ExecutionAveragePrice;
        await _db.SaveChangesAsync(cancellationToken);
        return replay;
    }

    /// <summary>將 entity 與 replay 結果組合成 mutation response。</summary>
    private static StockLedgerMutationResult CreateMutationResult(
        StockTransaction transaction,
        StockLedgerResult replay)
    {
        var entryResult = replay.Entries.Single(entry => entry.Entry.Id == transaction.Id);
        return new StockLedgerMutationResult(transaction, replay, entryResult);
    }

    /// <summary>將交易 entity 轉成 calculator 使用的純輸入 record。</summary>
    private static StockLedgerEntry ToEntry(StockTransaction transaction)
        => new(
            transaction.Id,
            transaction.Type,
            transaction.TradeDate,
            transaction.Sequence,
            transaction.Shares,
            transaction.Price,
            transaction.Fee,
            transaction.Tax,
            transaction.CashAmount,
            transaction.OpeningMarketValue,
            transaction.Notes);

    /// <summary>取得 UTC 現在時間並確保 audit 欄位不帶 local kind。</summary>
    private DateTime GetUtcNow()
        => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>失敗時回滾資料庫 transaction 並清除可能殘留的 tracked mutation。</summary>
    private async Task RollbackAndClearAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }
}
