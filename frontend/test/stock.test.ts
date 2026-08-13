import assert from 'node:assert/strict'
import { test } from 'node:test'
import { buildStocksQuery } from '../src/api/index.ts'
import {
  STOCK_INSTRUMENT_TYPE_OPTIONS,
  STOCK_MARKET_OPTIONS,
  formatStockInstrumentType,
  formatStockMarket,
} from '../src/utils/stock.ts'
import { syncStockPriceOnSave } from '../src/utils/stockPriceSync.ts'
import { applyStockMarketLookup, resetStockMarketLookupFields } from '../src/utils/stockMarketLookup.ts'

// Verifies stock instrument type labels match the Taiwan tax categories shown in the UI.
test('STOCK_INSTRUMENT_TYPE_OPTIONS lists supported instrument types', () => {
  assert.deepEqual(STOCK_INSTRUMENT_TYPE_OPTIONS, [
    { value: 'Stock', label: '股票' },
    { value: 'StockEtf', label: '股票型 ETF' },
    { value: 'BondEtf', label: '債券 ETF' },
  ])
})

// Verifies unknown or missing instrument type values are displayed as regular stocks by default.
test('formatStockInstrumentType returns labels with stock fallback', () => {
  assert.equal(formatStockInstrumentType('Stock'), '股票')
  assert.equal(formatStockInstrumentType('StockEtf'), '股票型 ETF')
  assert.equal(formatStockInstrumentType('BondEtf'), '債券 ETF')
  assert.equal(formatStockInstrumentType(undefined), '股票')
})

// 驗證交易市場選項及未知值的顯示文字符合股票管理介面。
test('STOCK_MARKET_OPTIONS exposes unknown, listed, and over-the-counter labels', () => {
  assert.deepEqual(STOCK_MARKET_OPTIONS, [
    { value: 'Unknown', label: '待辨識' },
    { value: 'Twse', label: '上市' },
    { value: 'Tpex', label: '上櫃' },
  ])
  assert.equal(formatStockMarket('Unknown'), '待辨識')
  assert.equal(formatStockMarket('Twse'), '上市')
  assert.equal(formatStockMarket('Tpex'), '上櫃')
  assert.equal(formatStockMarket(undefined), '待辨識')
})

// Verifies stock list queries include trimmed symbol and broker filters.
test('buildStocksQuery includes trimmed symbol and broker filters', () => {
  const query = new URLSearchParams(buildStocksQuery({ page: 2, pageSize: 15, symbol: ' 233 ', broker: ' 元大 ' }))

  assert.equal(query.get('page'), '2')
  assert.equal(query.get('pageSize'), '15')
  assert.equal(query.get('symbol'), '233')
  assert.equal(query.get('broker'), '元大')
})

// Verifies blank stock filters are omitted so the API returns all stocks.
test('buildStocksQuery omits blank stock filters', () => {
  const query = new URLSearchParams(buildStocksQuery({ page: 1, pageSize: 15, symbol: '   ', broker: ' ' }))

  assert.equal(query.get('page'), '1')
  assert.equal(query.get('pageSize'), '15')
  assert.equal(query.has('symbol'), false)
  assert.equal(query.has('broker'), false)
})

// 驗證停用即時查價時保留既有價格狀態，且不呼叫 lookup。
test('syncStockPriceOnSave skips lookup when disabled', async () => {
  let lookupCalls = 0
  const result = await syncStockPriceOnSave(
    false,
    '2330',
    { currentPrice: 1000, lastPriceUpdate: '2026-07-14T00:00:00.000Z' },
    async () => {
      lookupCalls++
      return { currentPrice: 1100, market: 'Twse' }
    },
    () => '2026-07-15T00:00:00.000Z',
    'Twse',
  )

  assert.equal(lookupCalls, 0)
  assert.deepEqual(result, {
    status: 'skipped',
    currentPrice: 1000,
    lastPriceUpdate: '2026-07-14T00:00:00.000Z',
  })
})

// 驗證相同明確市場的 lookup 會回傳新價格與完成時間。
test('syncStockPriceOnSave applies a successful lookup', async () => {
  let lookedUpSymbol = ''
  const result = await syncStockPriceOnSave(
    true,
    ' 2330 ',
    { currentPrice: 1000, lastPriceUpdate: '2026-07-14T00:00:00.000Z' },
    async (symbol) => {
      lookedUpSymbol = symbol
      return { currentPrice: 1100, market: 'Twse' }
    },
    () => '2026-07-15T00:00:00.000Z',
    'Twse',
  )

  assert.equal(lookedUpSymbol, '2330')
  assert.deepEqual(result, {
    status: 'succeeded',
    currentPrice: 1100,
    lastPriceUpdate: '2026-07-15T00:00:00.000Z',
  })
})

// 驗證 lookup 未提供價格時保留舊狀態並回報同步失敗。
test('syncStockPriceOnSave preserves state when lookup has no price', async () => {
  const result = await syncStockPriceOnSave(
    true,
    '2330',
    { currentPrice: 1000, lastPriceUpdate: '2026-07-14T00:00:00.000Z' },
    async () => ({ currentPrice: null, market: 'Twse' }),
    () => '2026-07-15T00:00:00.000Z',
    'Twse',
  )

  assert.deepEqual(result, {
    status: 'failed',
    currentPrice: 1000,
    lastPriceUpdate: '2026-07-14T00:00:00.000Z',
  })
})

// 驗證 lookup 發生例外時保留舊狀態並回報同步失敗。
test('syncStockPriceOnSave preserves state when lookup throws', async () => {
  const result = await syncStockPriceOnSave(
    true,
    '2330',
    { currentPrice: 1000, lastPriceUpdate: '2026-07-14T00:00:00.000Z' },
    async () => {
      throw new Error('TWSE unavailable')
    },
    () => '2026-07-15T00:00:00.000Z',
    'Twse',
  )

  assert.deepEqual(result, {
    status: 'failed',
    currentPrice: 1000,
    lastPriceUpdate: '2026-07-14T00:00:00.000Z',
  })
})

// 驗證空白代號不觸發 lookup 並保留舊狀態。
test('syncStockPriceOnSave fails without lookup for a blank symbol', async () => {
  let lookupCalls = 0
  const result = await syncStockPriceOnSave(
    true,
    '   ',
    { currentPrice: 1000, lastPriceUpdate: '2026-07-14T00:00:00.000Z' },
    async () => {
      lookupCalls++
      return { currentPrice: 1100, market: 'Twse' }
    },
    () => '2026-07-15T00:00:00.000Z',
    'Twse',
  )

  assert.equal(lookupCalls, 0)
  assert.deepEqual(result, {
    status: 'failed',
    currentPrice: 1000,
    lastPriceUpdate: '2026-07-14T00:00:00.000Z',
  })
})

// 驗證代號改變時只重設 lookup 衍生欄位，並保留使用者手動修改的欄位。
test('resetStockMarketLookupFields resets clean fields and preserves dirty fields', () => {
  assert.deepEqual(
    resetStockMarketLookupFields(
      { name: '台積電', symbol: '6488', market: 'Twse', currentPrice: 1000 },
      {},
    ),
    { name: '', symbol: '6488', market: 'Unknown', currentPrice: 0 },
  )
  assert.deepEqual(
    resetStockMarketLookupFields(
      { name: '自訂名稱', symbol: '6488', market: 'Tpex', currentPrice: 999 },
      { name: true, currentPrice: true, market: true },
    ),
    { name: '自訂名稱', symbol: '6488', market: 'Tpex', currentPrice: 999 },
  )
})

// 驗證儲存同步拒絕不同市場的價格，並保留原價格與更新時間。
test('syncStockPriceOnSave preserves state when lookup market mismatches', async () => {
  const result = await syncStockPriceOnSave(
    true,
    '2330',
    { currentPrice: 1000, lastPriceUpdate: '2026-07-14T00:00:00.000Z' },
    async () => ({ currentPrice: 1100, market: 'Twse' }),
    () => '2026-07-15T00:00:00.000Z',
    'Tpex',
  )

  assert.deepEqual(result, {
    status: 'failed',
    currentPrice: 1000,
    lastPriceUpdate: '2026-07-14T00:00:00.000Z',
  })
})

// 驗證使用者或 lookup 市場不明時，不會套用無法確認市場歸屬的價格。
test('syncStockPriceOnSave preserves state when either market is unknown', async () => {
  const existingState = { currentPrice: 1000, lastPriceUpdate: '2026-07-14T00:00:00.000Z' }
  const unknownExpectedMarket = await syncStockPriceOnSave(
    true,
    '2330',
    existingState,
    async () => ({ currentPrice: 1100, market: 'Twse' }),
    () => '2026-07-15T00:00:00.000Z',
    'Unknown',
  )
  const unknownLookupMarket = await syncStockPriceOnSave(
    true,
    '2330',
    existingState,
    async () => ({ currentPrice: 1100, market: 'Unknown' }),
    () => '2026-07-15T00:00:00.000Z',
    'Twse',
  )

  assert.deepEqual(unknownExpectedMarket, { status: 'failed', ...existingState })
  assert.deepEqual(unknownLookupMarket, { status: 'failed', ...existingState })
})

// 驗證唯一市場 lookup 會帶入名稱、價格與市場。
test('applyStockMarketLookup applies unique market result', () => {
  const result = applyStockMarketLookup(
    { name: '', symbol: '2330', market: 'Unknown', currentPrice: 0 },
    { name: '台積電', currentPrice: 1000, market: 'Twse', resultCode: 'Completed' },
    '2330',
    '2330',
    false,
  )

  assert.deepEqual(result, { name: '台積電', symbol: '2330', market: 'Twse', currentPrice: 1000 })
})

// 驗證未知 lookup 或使用者已修改市場時不會覆寫表單市場。
test('applyStockMarketLookup preserves unknown or dirty market', () => {
  const state = { name: '自訂名稱', symbol: '2330', market: 'Tpex' as const, currentPrice: 100 }
  const unknown = applyStockMarketLookup(
    state,
    { name: null, currentPrice: null, market: 'Unknown', resultCode: 'MarketNotFound' },
    '2330',
    '2330',
    false,
  )
  const dirty = applyStockMarketLookup(
    state,
    { name: '台積電', currentPrice: 1000, market: 'Twse', resultCode: 'Completed' },
    '2330',
    '2330',
    true,
  )

  assert.deepEqual(unknown, state)
  assert.equal(dirty.market, 'Tpex')
})

// 驗證使用者手動修改名稱或現價後，晚到 lookup 不會覆寫表單意圖。
test('applyStockMarketLookup preserves manually edited name and current price', () => {
  const result = applyStockMarketLookup(
    { name: '自訂名稱', symbol: '2330', market: 'Unknown', currentPrice: 999 },
    { name: '台積電', currentPrice: 1000, market: 'Twse', resultCode: 'Completed' },
    '2330',
    '2330',
    false,
    { name: true, currentPrice: true },
  )

  assert.deepEqual(result, {
    name: '自訂名稱',
    symbol: '2330',
    market: 'Twse',
    currentPrice: 999,
  })
})

// 驗證較舊 lookup response 不會覆寫新代號。
test('applyStockMarketLookup ignores response for a different symbol', () => {
  const state = { name: '', symbol: '6488', market: 'Unknown' as const, currentPrice: 0 }
  const result = applyStockMarketLookup(
    state,
    { name: '台積電', currentPrice: 1000, market: 'Twse', resultCode: 'Completed' },
    '2330',
    '6488',
    false,
  )

  assert.deepEqual(result, state)
})
