import type { CurrencyCode } from '../types'

/** 前端可選的固定支援幣別，需與後端 CurrencyPolicy 保持一致。 */
export const SUPPORTED_CURRENCY_CODES: readonly CurrencyCode[] = ['TWD', 'USD', 'JPY', 'CNY', 'HKD']

/** 顯示支援幣別名稱與代碼的固定選項。 */
export const CURRENCY_OPTIONS = SUPPORTED_CURRENCY_CODES.map(code => ({
  value: code,
  label: `${getCurrencySymbol(code)} ${getCurrencyName(code)} (${code})`,
}))

/** 回傳貨幣代碼對應的顯示名稱。 */
export function getCurrencyName(code: CurrencyCode): string {
  const names: Record<CurrencyCode, string> = {
    TWD: '新台幣',
    USD: '美元',
    JPY: '日圓',
    CNY: '人民幣',
    HKD: '港幣',
  }
  return names[code]
}

/** 回傳貨幣代碼對應的顯示符號。 */
export function getCurrencySymbol(code: CurrencyCode): string {
  const symbols: Record<CurrencyCode, string> = {
    TWD: 'NT$',
    USD: '$',
    JPY: '¥',
    CNY: '¥',
    HKD: 'HK$',
  }
  return symbols[code]
}
