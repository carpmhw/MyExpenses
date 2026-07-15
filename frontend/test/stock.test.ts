import assert from 'node:assert/strict'
import { test } from 'node:test'
import { buildStocksQuery } from '../src/api/index.ts'
import { STOCK_INSTRUMENT_TYPE_OPTIONS, formatStockInstrumentType } from '../src/utils/stock.ts'
import { syncStockPriceOnSave } from '../src/utils/stockPriceSync.ts'

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

// Verifies disabling immediate lookup preserves the existing price state without calling the lookup.
test('syncStockPriceOnSave skips lookup when disabled', async () => {
  let lookupCalls = 0
  const result = await syncStockPriceOnSave(
    false,
    '2330',
    { currentPrice: 1000, lastPriceUpdate: '2026-07-14T00:00:00.000Z' },
    async () => {
      lookupCalls++
      return { currentPrice: 1100 }
    },
    () => '2026-07-15T00:00:00.000Z',
  )

  assert.equal(lookupCalls, 0)
  assert.deepEqual(result, {
    status: 'skipped',
    currentPrice: 1000,
    lastPriceUpdate: '2026-07-14T00:00:00.000Z',
  })
})

// Verifies a successful lookup returns the new price and the lookup completion timestamp.
test('syncStockPriceOnSave applies a successful lookup', async () => {
  let lookedUpSymbol = ''
  const result = await syncStockPriceOnSave(
    true,
    ' 2330 ',
    { currentPrice: 1000, lastPriceUpdate: '2026-07-14T00:00:00.000Z' },
    async (symbol) => {
      lookedUpSymbol = symbol
      return { currentPrice: 1100 }
    },
    () => '2026-07-15T00:00:00.000Z',
  )

  assert.equal(lookedUpSymbol, '2330')
  assert.deepEqual(result, {
    status: 'succeeded',
    currentPrice: 1100,
    lastPriceUpdate: '2026-07-15T00:00:00.000Z',
  })
})

// Verifies missing lookup prices preserve the old state and report a failed synchronization.
test('syncStockPriceOnSave preserves state when lookup has no price', async () => {
  const result = await syncStockPriceOnSave(
    true,
    '2330',
    { currentPrice: 1000, lastPriceUpdate: '2026-07-14T00:00:00.000Z' },
    async () => ({ currentPrice: null }),
    () => '2026-07-15T00:00:00.000Z',
  )

  assert.deepEqual(result, {
    status: 'failed',
    currentPrice: 1000,
    lastPriceUpdate: '2026-07-14T00:00:00.000Z',
  })
})

// Verifies lookup exceptions preserve the old state and report a failed synchronization.
test('syncStockPriceOnSave preserves state when lookup throws', async () => {
  const result = await syncStockPriceOnSave(
    true,
    '2330',
    { currentPrice: 1000, lastPriceUpdate: '2026-07-14T00:00:00.000Z' },
    async () => {
      throw new Error('TWSE unavailable')
    },
    () => '2026-07-15T00:00:00.000Z',
  )

  assert.deepEqual(result, {
    status: 'failed',
    currentPrice: 1000,
    lastPriceUpdate: '2026-07-14T00:00:00.000Z',
  })
})

// Verifies blank symbols do not trigger a lookup and preserve the old state.
test('syncStockPriceOnSave fails without lookup for a blank symbol', async () => {
  let lookupCalls = 0
  const result = await syncStockPriceOnSave(
    true,
    '   ',
    { currentPrice: 1000, lastPriceUpdate: '2026-07-14T00:00:00.000Z' },
    async () => {
      lookupCalls++
      return { currentPrice: 1100 }
    },
    () => '2026-07-15T00:00:00.000Z',
  )

  assert.equal(lookupCalls, 0)
  assert.deepEqual(result, {
    status: 'failed',
    currentPrice: 1000,
    lastPriceUpdate: '2026-07-14T00:00:00.000Z',
  })
})
