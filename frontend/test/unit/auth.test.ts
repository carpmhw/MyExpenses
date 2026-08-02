import { beforeEach, describe, expect, it, vi } from 'vitest'
import { abortError, createFetchMock, deferred, jsonResponse } from '../support/deferred'

const user = { id: 1, email: 'user@example.com', displayName: 'User' }

describe('authentication bootstrap', () => {
  beforeEach(() => {
    vi.resetModules()
  })

  it('shares one status request across concurrent initialization calls', async () => {
    const status = deferred<Response>()
    const fetchMock = createFetchMock(() => status.promise)
    const { useAuth } = await import('../../src/composables/useAuth')
    const auth = useAuth()

    const first = auth.initialize()
    const second = auth.initialize()
    expect(auth.authState.value).toBe('unknown')
    expect(fetchMock).toHaveBeenCalledTimes(1)

    status.resolve(jsonResponse({ authenticated: true, user, hasUsers: true }))
    await Promise.all([first, second])

    expect(auth.authState.value).toBe('authenticated')
    expect(auth.isAuthenticated.value).toBe(true)
  })

  it('does not let an older status response replace a newer login', async () => {
    const status = deferred<Response>()
    createFetchMock(() => status.promise)
    const { useAuth } = await import('../../src/composables/useAuth')
    const auth = useAuth()

    const initialization = auth.initialize()
    auth.setAuth('new-token', user)
    status.resolve(jsonResponse({ authenticated: false, user: null, hasUsers: true }))
    await initialization

    expect(auth.authState.value).toBe('authenticated')
    expect(auth.token.value).toBe('new-token')
  })

  it('clears local authentication synchronously before best-effort logout completes', async () => {
    const logout = deferred<Response>()
    createFetchMock((input) => String(input).endsWith('/auth/logout') ? logout.promise : jsonResponse({ authenticated: true, user, hasUsers: true }))
    const { useAuth } = await import('../../src/composables/useAuth')
    const auth = useAuth()
    auth.setAuth('token', user)

    const pendingLogout = auth.logout()

    expect(auth.authState.value).toBe('guest')
    expect(auth.token.value).toBeNull()
    logout.resolve(jsonResponse({ message: 'ok' }))
    await pendingLogout
  })

  it('expires the current session once when a protected request receives 401', async () => {
    createFetchMock(() => jsonResponse({ title: 'Unauthorized' }, 401))
    const { useAuth } = await import('../../src/composables/useAuth')
    const { request } = await import('../../src/api')
    const auth = useAuth()
    auth.setAuth('token', user)

    await Promise.all([
      request('/protected').catch(() => undefined),
      request('/protected').catch(() => undefined),
    ])

    expect(auth.authState.value).toBe('guest')
  })

  it('keeps network initialization unknown state from masquerading as authenticated data', async () => {
    createFetchMock(() => Promise.reject(abortError()))
    const { useAuth } = await import('../../src/composables/useAuth')
    const auth = useAuth()

    await auth.initialize()

    expect(auth.authState.value).toBe('guest')
    expect(auth.isAuthenticated.value).toBe(false)
  })
})
