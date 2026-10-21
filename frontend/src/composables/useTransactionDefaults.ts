import { computed, ref, watch } from 'vue'
import { api } from '../api'
import type { TransactionDefaults } from '../types'
import { useAsyncQuery } from './useAsyncQuery'
import { useAsyncMutation } from './useAsyncMutation'

export type DefaultResource = 'bankAccounts' | 'creditCards'

/** 管理單一類別的伺服器固定預設，保留成功寫入與後續重新整理的不同結果。 */
export function useTransactionDefaults(resource: DefaultResource) {
  const confirmed = ref<TransactionDefaults>()
  const query = useAsyncQuery({
    key: () => ['transaction-defaults', resource],
    query: ({ signal }) => api.settings.getTransactionDefaults({ signal }),
  })
  // 僅接受 query stream 已確認的回應，不將未知或失敗狀態轉成空預設。
  watch(query.data, value => { if (value) confirmed.value = value })
  const mutation = useAsyncMutation<number, TransactionDefaults>({
    mutate: id => api[resource].setDefault(id),
    // 先保留成功寫入，再獨立讀取最新狀態；過期 GET 不可覆蓋已確認的寫入。
    onSuccess: async result => {
      query.cancel()
      confirmed.value = result
      await query.refresh()
      if (query.error.value) throw query.error.value
    },
  })
  const defaultId = computed(() => resource === 'bankAccounts'
    ? confirmed.value?.defaultBankAccountId : confirmed.value?.defaultCreditCardId)
  const isSaving = computed(() => mutation.status.value === 'submitting' || query.isInFlight.value)
  const message = computed(() => {
    if (mutation.error.value) return '設定預設失敗，請重試。'
    if (mutation.followUpError.value) return '預設已設定，但重新整理失敗，請重試。'
    if (query.error.value) return '無法確認預設設定，請重試。'
    return ''
  })

  /** 提交明確的預設目標，失敗由行內訊息呈現，避免未處理的事件例外。 */
  async function setDefault(id: number): Promise<void> {
    try { await mutation.submit(id) } catch { /* 保留 mutation 的錯誤供介面呈現。 */ }
  }

  /** 重新查詢預設並清除已結束的寫入錯誤，不預先移除目前標示。 */
  async function refresh(): Promise<void> {
    mutation.reset()
    await query.refresh()
  }

  return { defaultId, isSaving, message, setDefault, refresh }
}
