import { onScopeDispose, ref, watch, type Ref, type WatchStopHandle } from 'vue'
import { isRequestCancelled } from '../api'
import { normalizeQueryKey } from '../utils/queryKey'

export type AsyncQueryStatus = 'idle' | 'loading' | 'success' | 'empty' | 'refreshing' | 'stale' | 'error'

export interface AsyncQueryOptions<T> {
  key: () => unknown
  query: (context: { signal: AbortSignal }) => Promise<T>
  isEmpty?: (value: T) => boolean
  now?: () => number
  immediate?: boolean
}

export interface AsyncQueryState<T> {
  status: Ref<AsyncQueryStatus>
  data: Ref<T | undefined>
  error: Ref<unknown | null>
  activeKey: Ref<string | null>
  lastSuccessAt: Ref<number | null>
  isInFlight: Ref<boolean>
  refresh: () => Promise<void>
  retry: () => Promise<void>
  cancel: () => void
  dispose: () => void
}

// Owns one cancellable query stream and prevents stale generations from mutating its state.
export function useAsyncQuery<T>(options: AsyncQueryOptions<T>): AsyncQueryState<T> {
  const status = ref<AsyncQueryStatus>(options.immediate === false ? 'idle' : 'loading')
  const data = ref<T | undefined>()
  const error = ref<unknown | null>(null)
  const activeKey = ref<string | null>(null)
  const lastSuccessAt = ref<number | null>(null)
  const isInFlight = ref(false)
  const now = options.now ?? Date.now
  let generation = 0
  let activeController: AbortController | null = null
  let dataKey: string | null = null
  let disposed = false
  let stopWatching: WatchStopHandle = () => undefined

  // Executes one query generation and applies only current response state transitions.
  async function execute(queryKey: string): Promise<void> {
    const currentGeneration = ++generation
    const sameKey = dataKey === queryKey && data.value !== undefined
    activeController?.abort()
    const controller = new AbortController()
    activeController = controller
    isInFlight.value = true
    activeKey.value = queryKey
    error.value = null
    if (sameKey) {
      status.value = 'refreshing'
    } else {
      data.value = undefined
      dataKey = null
      status.value = 'loading'
    }

    try {
      const result = await options.query({ signal: controller.signal })
      if (disposed || currentGeneration !== generation) return
      data.value = result
      dataKey = queryKey
      lastSuccessAt.value = now()
      status.value = options.isEmpty?.(result) ? 'empty' : 'success'
    } catch (queryError) {
      if (disposed || currentGeneration !== generation || isRequestCancelled(queryError)) return
      error.value = queryError
      status.value = sameKey && dataKey === queryKey && data.value !== undefined ? 'stale' : 'error'
    } finally {
      if (currentGeneration === generation && activeController === controller) {
        activeController = null
        isInFlight.value = false
      }
    }
  }

  // Refreshes the current normalized query while retaining same-key data during loading.
  async function refresh(): Promise<void> {
    await execute(normalizeQueryKey(options.key()))
  }

  // Retries the active query identity after an initial or stale failure.
  async function retry(): Promise<void> {
    await execute(activeKey.value ?? normalizeQueryKey(options.key()))
  }

  // 取消目前 request 但保留 query stream，供頁面重新可見時再次 refresh。
  function cancel(): void {
    generation++
    activeController?.abort()
    activeController = null
    isInFlight.value = false
  }

  // Aborts the owned request and detaches future state transitions permanently.
  function dispose(): void {
    if (disposed) return
    cancel()
    disposed = true
    stopWatching()
  }

  if (options.immediate !== false) {
    stopWatching = watch(
      () => normalizeQueryKey(options.key()),
      queryKey => { void execute(queryKey) },
      { immediate: true },
    )
  } else {
    activeKey.value = normalizeQueryKey(options.key())
  }
  onScopeDispose(dispose)

  return { status, data, error, activeKey, lastSuccessAt, isInFlight, refresh, retry, cancel, dispose }
}
