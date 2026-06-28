export function formatMoney(amount: number): string {
  return `NT$ ${amount.toLocaleString()}`
}

/**
 * 格式化股數，1000 股以上顯示為張數。
 */
export function formatShares(shares: number): string {
  if (shares >= 1000) {
    const lots = shares / 1000
    return `${lots.toLocaleString(undefined, { maximumFractionDigits: 2 })} 張`
  }
  return `${shares.toLocaleString()} 股`
}
