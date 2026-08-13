import type { StockMarket } from '../types'

export interface StockPriceState {
  currentPrice: number
  lastPriceUpdate: string | null
}

export interface StockPriceLookupResult {
  currentPrice: number | null
  market: StockMarket
  resultCode?: string
}

export type StockPriceLookup = (symbol: string) => Promise<StockPriceLookupResult>
export type StockPriceSyncStatus = 'skipped' | 'succeeded' | 'failed'

export interface StockPriceSyncResult extends StockPriceState {
  status: StockPriceSyncStatus
}

/** 僅在 lookup 市場符合預期明確市場時套用一次性股價，失敗則保留既有狀態。 */
export async function syncStockPriceOnSave(
  enabled: boolean,
  symbol: string,
  existingState: StockPriceState,
  lookup: StockPriceLookup,
  now: () => string,
  expectedMarket: StockMarket,
): Promise<StockPriceSyncResult> {
  if (!enabled) {
    return { status: 'skipped', ...existingState }
  }

  const normalizedSymbol = symbol.trim()
  if (!normalizedSymbol) {
    return { status: 'failed', ...existingState }
  }

  try {
    const result = await lookup(normalizedSymbol)
    if (
      result.currentPrice == null
      || expectedMarket === 'Unknown'
      || result.market === 'Unknown'
      || result.market !== expectedMarket
    ) {
      return { status: 'failed', ...existingState }
    }

    return {
      status: 'succeeded',
      currentPrice: result.currentPrice,
      lastPriceUpdate: now(),
    }
  } catch {
    return { status: 'failed', ...existingState }
  }
}
