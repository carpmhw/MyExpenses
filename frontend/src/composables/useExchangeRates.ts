import { ref } from 'vue'

/**
 * 匯率 API 回應介面
 */
interface ExchangeRateResponse {
  base: string
  rates: Record<string, number>
  updatedAt: string
  warning?: string
}

/**
 * 匯率查詢 composable，封裝 API 請求、載入/錯誤狀態。
 */
export function useExchangeRates() {
  const rates = ref<Record<string, number>>({})
  const updatedAt = ref<string>('')
  const loading = ref(false)
  const error = ref<string | null>(null)
  const warning = ref<string | null>(null)

  /**
   * 從後端獲取最新匯率資料。
   */
  async function fetchRates(): Promise<void> {
    loading.value = true
    error.value = null
    warning.value = null

    try {
      const token = localStorage.getItem('authToken')
      const headers: Record<string, string> = { 'Content-Type': 'application/json' }
      if (token) {
        headers['Authorization'] = `Bearer ${token}`
      }

      const response = await fetch('/api/exchange-rates', { headers })
      
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}: ${response.statusText}`)
      }

      const data: ExchangeRateResponse = await response.json()
      
      rates.value = data.rates
      updatedAt.value = data.updatedAt
      
      if (data.warning) {
        warning.value = data.warning
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : '未知錯誤'
      error.value = `無法獲取匯率資料: ${message}`
      
      // 如果有舊的匯率資料，保留它們
      if (Object.keys(rates.value).length === 0) {
        // 完全沒有資料
        rates.value = {}
        updatedAt.value = ''
      }
    } finally {
      loading.value = false
    }
  }

  /**
   * 將金額從一種貨幣轉換為另一種貨幣。
   */
  function convert(amount: number, fromCurrency: string, toCurrency: string): number | null {
    if (amount <= 0 || isNaN(amount)) return null
    if (fromCurrency === toCurrency) return amount

    const rateFrom = rates.value[fromCurrency]
    const rateTo = rates.value[toCurrency]

    if (!rateFrom || !rateTo) return null

    // 所有匯率都是相對於 TWD 的
    // 從 fromCurrency 到 TWD：除以 fromCurrency 的匯率
    // 從 TWD 到 toCurrency：乘以 toCurrency 的匯率
    const amountInTWD = amount / rateFrom
    const result = amountInTWD * rateTo

    return result
  }

  /**
   * 格式化貨幣金額。
   */
  function formatAmount(amount: number, currency: string): string {
    const formatter = new Intl.NumberFormat('zh-TW', {
      style: 'currency',
      currency: currency === 'TWD' ? 'TWD' : currency,
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    })
    return formatter.format(amount)
  }

  /**
   * 獲取貨幣符號。
   */
  function getCurrencySymbol(currency: string): string {
    const symbols: Record<string, string> = {
      TWD: 'NT$',
      USD: '$',
      JPY: '¥',
      CNY: '¥',
      HKD: 'HK$',
    }
    return symbols[currency] || currency
  }

  /**
   * 獲取貨幣名稱。
   */
  function getCurrencyName(currency: string): string {
    const names: Record<string, string> = {
      TWD: '新台幣',
      USD: '美元',
      JPY: '日圓',
      CNY: '人民幣',
      HKD: '港幣',
    }
    return names[currency] || currency
  }

  return {
    rates,
    updatedAt,
    loading,
    error,
    warning,
    fetchRates,
    convert,
    formatAmount,
    getCurrencySymbol,
    getCurrencyName,
  }
}