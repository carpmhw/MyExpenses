import type { StockInstrumentType, StockMarket } from '../types'

export const STOCK_INSTRUMENT_TYPE_OPTIONS: { value: StockInstrumentType; label: string }[] = [
  { value: 'Stock', label: '股票' },
  { value: 'StockEtf', label: '股票型 ETF' },
  { value: 'BondEtf', label: '債券 ETF' },
]

export const STOCK_MARKET_OPTIONS: { value: StockMarket; label: string }[] = [
  { value: 'Unknown', label: '待辨識' },
  { value: 'Twse', label: '上市' },
  { value: 'Tpex', label: '上櫃' },
]

// Formats stock instrument type values for Taiwan stock and ETF labels.
export function formatStockInstrumentType(value: StockInstrumentType | undefined | null): string {
  return STOCK_INSTRUMENT_TYPE_OPTIONS.find((option) => option.value === value)?.label ?? '股票'
}

// 將交易市場 enum 轉成股票管理頁使用的中文標籤。
export function formatStockMarket(value: StockMarket | undefined | null): string {
  return STOCK_MARKET_OPTIONS.find((option) => option.value === value)?.label ?? '待辨識'
}
