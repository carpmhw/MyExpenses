import { onScopeDispose, ref } from 'vue'
import { ApiError, api, isRequestCancelled } from '../api'

/**
 * 匯率查詢 composable，封裝 API 請求、載入/錯誤狀態。
 */
export function useExchangeRates() {
  const rates = ref<Record<string, number>>({})
  const updatedAt = ref<string>('')
  const loading = ref(false)
  const error = ref<string | null>(null)
  const warning = ref<string | null>(null)
  let generation = 0
  let activeController: AbortController | null = null

  /**
   * 從後端獲取最新匯率資料。
   */
  async function fetchRates(): Promise<void> {
    const currentGeneration = ++generation
    activeController?.abort()
    const controller = new AbortController()
    activeController = controller
    const hadData = Object.keys(rates.value).length > 0
    loading.value = true
    error.value = null
    warning.value = null

    try {
      const data = await api.exchangeRates.get({ signal: controller.signal })
      if (currentGeneration !== generation) return
      rates.value = data.rates
      updatedAt.value = data.updatedAt
      if (data.warning) {
        warning.value = data.warning
      }
    } catch (err) {
      if (currentGeneration !== generation || isRequestCancelled(err)) return
      error.value = err instanceof ApiError ? err.userMessage : '無法獲取匯率資料，請稍後再試'
      if (!hadData) {
        rates.value = {}
        updatedAt.value = ''
      }
    } finally {
      if (currentGeneration === generation) loading.value = false
    }
  }

  // Aborts the owned request when the dialog or consuming scope is disposed.
  onScopeDispose(() => {
    generation++
    activeController?.abort()
    activeController = null
  })

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
