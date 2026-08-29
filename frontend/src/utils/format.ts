import type { CurrencyCode } from '../types'

/** 以 zh-TW locale 格式化支援幣別，null 表示目前無法換算。 */
export function formatCurrency(amount: number | null | undefined, currencyCode: CurrencyCode): string {
  if (amount === null || amount === undefined) return '不可用'

  return new Intl.NumberFormat('zh-TW', {
    style: 'currency',
    currency: currencyCode,
  }).format(amount)
}

export function formatMoney(amount: number): string {
  return `NT$ ${amount.toLocaleString()}`
}

/**
 * 格式化股數，1000 股以上顯示為張數。
 */
export function formatShares(shares: number): string {
  if (shares >= 1000) {
    const lots = Math.floor((shares / 1000) * 100) / 100
    return `${lots.toLocaleString(undefined, { maximumFractionDigits: 2 })} 張`
  }
  return `${shares.toLocaleString()} 股`
}
