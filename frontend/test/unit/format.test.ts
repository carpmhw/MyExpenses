import { describe, expect, it } from 'vitest'
import { formatShares } from '../../src/utils/format'

describe('formatShares', () => {
  // 驗證整數張數在換算後維持正確單位與數值。
  it('formats the exact one-lot boundary', () => {
    expect(formatShares(1000)).toBe('1 張')
  })

  // 驗證超過一張且會進位的股數只截斷，不會四捨五入。
  it('truncates large holdings instead of rounding', () => {
    expect(formatShares(1299)).toBe('1.29 張')
    expect(formatShares(1999.99)).toBe('1.99 張')
  })

  // 驗證格式化只產生文字，不會修改來源股數。
  it('does not mutate the source share count', () => {
    const shares = 1299

    formatShares(shares)

    expect(shares).toBe(1299)
  })

  // 驗證一張以下的持股維持以股數顯示。
  it('keeps holdings below one lot as shares', () => {
    expect(formatShares(999)).toBe('999 股')
  })
})
