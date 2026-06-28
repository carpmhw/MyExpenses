import type { StockInstrumentType } from '../types'

export const STOCK_INSTRUMENT_TYPE_OPTIONS: { value: StockInstrumentType; label: string }[] = [
  { value: 'Stock', label: '股票' },
  { value: 'StockEtf', label: '股票型 ETF' },
  { value: 'BondEtf', label: '債券 ETF' },
]

// Formats stock instrument type values for Taiwan stock and ETF labels.
export function formatStockInstrumentType(value: StockInstrumentType | undefined | null): string {
  return STOCK_INSTRUMENT_TYPE_OPTIONS.find((option) => option.value === value)?.label ?? '股票'
}
