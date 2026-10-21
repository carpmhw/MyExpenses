import { computed, type Ref } from 'vue'
import { api, ApiError, type ApiRequestContext } from '../api'
import { useAsyncQuery } from './useAsyncQuery'
import type { DefaultResource } from './useTransactionDefaults'

interface FormDefaultOptions<T extends { id: number }> {
  resource: DefaultResource
  items: Ref<T[]>
  loadOptions: (signal: AbortSignal) => Promise<boolean>
  get: (id: number, context?: ApiRequestContext) => Promise<T>
  apply: (id: number) => void
  canApply: () => boolean
}

/** 每次新增獨立讀取伺服器預設，保護手動選擇、已提交命令與後續表單。 */
export function useFormDefault<T extends { id: number }>(options: FormDefaultOptions<T>) {
  let session = 0
  let touched = false
  let frozen = false
  const query = useAsyncQuery<T | null>({
    key: () => ['form-default', options.resource, session],
    immediate: false,
    // 同時取得最新設定與選項，再解析不在前 100 筆的有效預設。
    query: async ({ signal }) => {
      const [defaults, ready] = await Promise.all([
        api.settings.getTransactionDefaults({ signal }),
        options.loadOptions(signal),
      ])
      if (!ready) throw new Error('參考資料無法使用')
      const id = options.resource === 'bankAccounts' ? defaults.defaultBankAccountId : defaults.defaultCreditCardId
      if (id === null) return null
      return options.items.value.find(item => item.id === id) ?? await options.get(id, { signal })
    },
  })
  const errorMessage = computed(() => {
    if (!query.error.value) return ''
    return query.error.value instanceof ApiError && query.error.value.status === 404
      ? '預設項目已不存在，請手動選擇。'
      : '預設設定載入失敗，請手動選擇或重試。'
  })

  /** 僅將目前 session 的成功回應套用至未被操作、未提交的新增表單。 */
  async function load(): Promise<void> {
    const current = session
    await query.refresh()
    if (current !== session || touched || frozen || !options.canApply() || query.status.value !== 'success') return
    const item = query.data.value
    if (!item) return
    if (!options.items.value.some(existing => existing.id === item.id)) options.items.value.push(item)
    options.apply(item.id)
  }

  /** 開啟新表單時建立新身分，不沿用前次預設或錯誤。 */
  function begin(): void {
    session++
    touched = false
    frozen = false
    void load()
  }

  /** 記錄使用者已決定選擇或清空，重試亦不可覆蓋。 */
  function touch(): void { touched = true }

  /** 金融提交開始後凍結自動預選，避免改變已準備的 payload 與冪等鍵。 */
  function freeze(): void { frozen = true; query.cancel() }

  /** 關閉或編輯時讓所有舊的新增查詢失效。 */
  function close(): void { session++; query.cancel() }

  return { begin, touch, freeze, close, retry: load, errorMessage, loading: query.isInFlight }
}
