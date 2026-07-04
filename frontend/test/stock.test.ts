import assert from 'node:assert/strict'
import { test } from 'node:test'
import { buildStocksQuery } from '../src/api/index.ts'
import { STOCK_INSTRUMENT_TYPE_OPTIONS, formatStockInstrumentType } from '../src/utils/stock.ts'

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
