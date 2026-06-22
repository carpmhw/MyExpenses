import { ref, type Ref } from 'vue'

export function useApi<T>(fetcher: () => Promise<T>) {
  const data: Ref<T | null> = ref(null)
  const loading = ref(false)
  const error: Ref<string | null> = ref(null)

  async function execute() {
    loading.value = true
    error.value = null
    try {
      data.value = await fetcher()
    } catch (e) {
      error.value = e instanceof Error ? e.message : '發生錯誤'
    } finally {
      loading.value = false
    }
  }

  return { data, loading, error, execute }
}
