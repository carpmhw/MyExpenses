import { effectScope, ref } from 'vue'
import { describe, expect, it, vi } from 'vitest'
import { useAsyncQuery } from '../../src/composables/useAsyncQuery'
import { deferred } from '../support/deferred'

async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
}

describe('useAsyncQuery', () => {
  it('transitions from loading to success or empty with the actual success timestamp', async () => {
    const pending = deferred<number[]>()
    const now = vi.fn(() => 1234)
    const query = useAsyncQuery({
      key: () => ['items', 1],
      query: () => pending.promise,
      isEmpty: value => value.length === 0,
      now,
    })

    expect(query.status.value).toBe('loading')
    pending.resolve([1])
    await flushPromises()

    expect(query.status.value).toBe('success')
    expect(query.data.value).toEqual([1])
    expect(query.lastSuccessAt.value).toBe(1234)
    expect(now).toHaveBeenCalledTimes(1)
  })

  it('retains same-key data during refresh and exposes stale state after refresh failure', async () => {
    const refresh = deferred<number[]>()
    let callCount = 0
    const query = useAsyncQuery({
      key: () => ['items'],
      query: () => {
        callCount++
        return callCount === 1 ? Promise.resolve([1]) : refresh.promise
      },
    })
    await flushPromises()

    const refreshing = query.refresh()
    expect(query.status.value).toBe('refreshing')
    expect(query.data.value).toEqual([1])
    refresh.reject(new Error('offline'))
    await refreshing

    expect(query.status.value).toBe('stale')
    expect(query.data.value).toEqual([1])
    expect(query.error.value).toBeInstanceOf(Error)
  })

  it('clears old data on key change and ignores old response and finally handlers', async () => {
    const key = ref('A')
    const first = deferred<string>()
    const second = deferred<string>()
    const query = useAsyncQuery({
      key: () => ['resource', key.value],
      query: ({ signal }) => key.value === 'A'
        ? first.promise.then(value => {
          if (signal.aborted) return value
          return value
        })
        : second.promise,
    })

    key.value = 'B'
    await flushPromises()
    expect(query.status.value).toBe('loading')
    expect(query.data.value).toBeUndefined()
    second.resolve('new')
    await flushPromises()
    first.resolve('old')
    await flushPromises()

    expect(query.data.value).toBe('new')
    expect(query.status.value).toBe('success')
  })

  it('aborts the owned request on scope disposal without entering error state', async () => {
    const pending = deferred<string>()
    let receivedSignal: AbortSignal | undefined
    const scope = effectScope()
    let query!: ReturnType<typeof useAsyncQuery<string>>
    scope.run(() => {
      query = useAsyncQuery({
        key: () => ['resource'],
        query: ({ signal }) => {
          receivedSignal = signal
          return pending.promise
        },
      })
    })

    scope.stop()
    pending.reject(new Error('aborted'))
    await flushPromises()

    expect(receivedSignal?.aborted).toBe(true)
    expect(query.status.value).toBe('loading')
  })

  it('retries a failed initial query', async () => {
    const retry = deferred<string>()
    let callCount = 0
    const query = useAsyncQuery({
      key: () => ['retry'],
      query: () => {
        callCount++
        return callCount === 1 ? Promise.reject(new Error('failed')) : retry.promise
      },
    })
    await flushPromises()
    expect(query.status.value).toBe('error')

    const retryPromise = query.retry()
    expect(query.status.value).toBe('loading')
    retry.resolve('ok')
    await retryPromise
    expect(query.status.value).toBe('success')
    expect(query.data.value).toBe('ok')
  })
})
