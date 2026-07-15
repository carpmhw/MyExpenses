export interface StockPriceState {
  currentPrice: number
  lastPriceUpdate: string | null
}

export interface StockPriceLookupResult {
  currentPrice: number | null
}

export type StockPriceLookup = (symbol: string) => Promise<StockPriceLookupResult>
export type StockPriceSyncStatus = 'skipped' | 'succeeded' | 'failed'

export interface StockPriceSyncResult extends StockPriceState {
  status: StockPriceSyncStatus
}

/** Resolves the one-time stock price lookup result while preserving the existing state on failure. */
export async function syncStockPriceOnSave(
  enabled: boolean,
  symbol: string,
  existingState: StockPriceState,
  lookup: StockPriceLookup,
  now: () => string,
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
    if (result.currentPrice == null) {
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
