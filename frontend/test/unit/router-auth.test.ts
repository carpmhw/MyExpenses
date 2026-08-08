import { beforeEach, describe, expect, it, vi } from 'vitest'
import { deferred, createFetchMock, jsonResponse } from '../support/deferred'

async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
}

describe('router authentication guard', () => {
  beforeEach(() => {
    vi.resetModules()
  })

  it('waits for unknown authentication before redirecting protected navigation', async () => {
    const status = deferred<Response>()
    const fetchMock = createFetchMock(() => status.promise)
    const { default: router } = await import('../../src/router')

    const navigation = router.push('/dashboard')
    await flushPromises()
    await new Promise(resolve => setTimeout(resolve, 0))

    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(router.currentRoute.value.path).not.toBe('/dashboard')

    status.resolve(jsonResponse({ authenticated: false, user: null, hasUsers: true }))
    await navigation

    expect(router.currentRoute.value.path).toBe('/login')
  })

  it('registers the schedule route as a protected lazy page', async () => {
    const { default: router } = await import('../../src/router')
    const route = router.getRoutes().find(item => item.path === '/schedules')

    expect(route?.name).toBe('schedules')
    expect(route?.meta.public).not.toBe(true)
    expect(route?.components?.default).toBeTypeOf('function')
  })
})
