import type { StockMarket } from '../types'

export interface StockMarketLookupResult {
  name: string | null
  currentPrice: number | null
  market: StockMarket
  resultCode: string
}

export interface StockLookupFormState {
  name: string
  symbol: string
  market: StockMarket
  currentPrice: number
}

export interface StockLookupDirtyState {
  name?: boolean
  currentPrice?: boolean
  market?: boolean
}

/** 代號改變時重設未經使用者修改的 lookup 衍生欄位。 */
export function resetStockMarketLookupFields(
  state: StockLookupFormState,
  dirtyState: StockLookupDirtyState = {},
): StockLookupFormState {
  return {
    ...state,
    name: dirtyState.name ? state.name : '',
    currentPrice: dirtyState.currentPrice ? state.currentPrice : 0,
    market: dirtyState.market ? state.market : 'Unknown',
  }
}

/** 將唯一市場 lookup 結果套用到新增表單，保留使用者已修改的欄位。 */
export function applyStockMarketLookup(
  state: StockLookupFormState,
  result: StockMarketLookupResult,
  requestSymbol: string,
  currentSymbol: string,
  marketDirty: boolean,
  dirtyState: StockLookupDirtyState = {},
): StockLookupFormState {
  if (requestSymbol.trim().toUpperCase() !== currentSymbol.trim().toUpperCase())
    return state

  return {
    ...state,
    name: dirtyState.name ? state.name : result.name ?? state.name,
    currentPrice: dirtyState.currentPrice ? state.currentPrice : result.currentPrice ?? state.currentPrice,
    market: marketDirty || result.market === 'Unknown' ? state.market : result.market,
  }
}
