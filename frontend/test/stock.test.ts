import assert from 'node:assert/strict'
import { test } from 'node:test'
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
