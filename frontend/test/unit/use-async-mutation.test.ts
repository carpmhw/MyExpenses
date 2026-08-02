import { describe, expect, it, vi } from 'vitest'
import { useAsyncMutation } from '../../src/composables/useAsyncMutation'
import { deferred } from '../support/deferred'

describe('useAsyncMutation', () => {
  it('prevents duplicate submits and exposes canonical success', async () => {
    const pending = deferred<{ id: number }>()
    const mutate = vi.fn(() => pending.promise)
    const mutation = useAsyncMutation({ mutate })

    const first = mutation.submit({ amount: 100 })
    const second = mutation.submit({ amount: 100 })
    expect(first).toBe(second)
    expect(mutate).toHaveBeenCalledTimes(1)
    expect(mutation.status.value).toBe('submitting')

    pending.resolve({ id: 1 })
    await first
    expect(mutation.status.value).toBe('success')
    expect(mutation.data.value).toEqual({ id: 1 })
  })

  it('keeps validation failures certain and exposes the error', async () => {
    const mutation = useAsyncMutation({
      mutate: () => Promise.reject(new Error('欄位錯誤')),
      classifyError: () => ({ uncertain: false }),
    })

    await mutation.submit({}).catch(() => undefined)

    expect(mutation.status.value).toBe('error')
    expect(mutation.error.value).toBeInstanceOf(Error)
    expect(mutation.uncertain.value).toBe(false)
  })

  it('marks network failures uncertain so callers can retain retry identity', async () => {
    const mutation = useAsyncMutation({
      mutate: () => Promise.reject(new Error('offline')),
      classifyError: () => ({ uncertain: true }),
    })

    await mutation.submit({}).catch(() => undefined)

    expect(mutation.status.value).toBe('error')
    expect(mutation.uncertain.value).toBe(true)
  })

  it('keeps mutation success independent from a failing post-success callback', async () => {
    const onSuccess = vi.fn(() => Promise.reject(new Error('refresh failed')))
    const mutation = useAsyncMutation({
      mutate: () => Promise.resolve({ id: 1 }),
      onSuccess,
    })

    await mutation.submit({})

    expect(onSuccess).toHaveBeenCalledWith({ id: 1 })
    expect(mutation.status.value).toBe('success')
    expect(mutation.data.value).toEqual({ id: 1 })
  })
})
