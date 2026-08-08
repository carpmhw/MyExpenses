import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  ApiError,
  RequestCancelledError,
  api,
  configureApiSession,
  request,
} from '../../src/api'
import { abortError, createFetchMock, jsonResponse } from '../support/deferred'

describe('central API client', () => {
  beforeEach(() => {
    configureApiSession({
      getToken: () => localStorage.getItem('authToken'),
      onSessionExpired: () => undefined,
    })
  })

  it('forwards AbortSignal to fetch', async () => {
    const controller = new AbortController()
    const fetchMock = createFetchMock(() => jsonResponse({ ok: true }))

    await request<{ ok: boolean }>('/test', { signal: controller.signal })

    expect(fetchMock).toHaveBeenCalledWith('/api/test', expect.objectContaining({
      signal: controller.signal,
    }))
  })

  it('sends the bootstrap secret in its dedicated header without persisting it', async () => {
    const bootstrapSecret = 'bootstrap-secret-generated-by-the-operator-123456'
    const fetchMock = createFetchMock(() => jsonResponse({ token: 'token', user: { id: 1 } }))

    await api.auth.register({
      email: 'owner@example.com',
      displayName: 'Owner',
      password: 'Valid!Password123',
    }, bootstrapSecret)

    const requestInit = fetchMock.mock.calls[0]?.[1]
    expect(new Headers(requestInit?.headers).get('X-MyExpenses-Bootstrap-Secret')).toBe(bootstrapSecret)
    expect(localStorage.getItem('bootstrapSecret')).toBeNull()
  })

  it('parses ProblemDetails into a typed safe error', async () => {
    createFetchMock(() => jsonResponse({
      title: 'Invalid request',
      detail: '請修正欄位',
      errors: { amount: ['金額必須大於零'] },
      traceId: 'trace-123',
    }, 422))

    const error = await request('/test').catch(value => value)

    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({
      status: 422,
      title: 'Invalid request',
      detail: '請修正欄位',
      fieldErrors: { amount: ['金額必須大於零'] },
      traceId: 'trace-123',
      userMessage: '請修正欄位',
    })
  })

  it('uses a safe fallback for non-JSON failures without exposing the raw body', async () => {
    createFetchMock(() => new Response('SQL exception and stack trace', {
      status: 500,
      headers: { 'Content-Type': 'text/plain' },
    }))

    const error = await request('/test').catch(value => value)

    expect(error).toBeInstanceOf(ApiError)
    expect(error.userMessage).toBe('伺服器目前無法處理要求')
    expect(error.userMessage).not.toContain('SQL')
  })

  it('classifies intentional aborts separately from API failures', async () => {
    createFetchMock(() => Promise.reject(abortError()))

    const error = await request('/test').catch(value => value)

    expect(error).toBeInstanceOf(RequestCancelledError)
    expect(error).not.toBeInstanceOf(ApiError)
  })

  it('does not expire a newer session when an older token receives 401', async () => {
    let currentToken = 'token-A'
    const onSessionExpired = vi.fn()
    configureApiSession({ getToken: () => currentToken, onSessionExpired })
    createFetchMock(() => {
      currentToken = 'token-B'
      return jsonResponse({ title: 'Unauthorized' }, 401)
    })

    await request('/protected').catch(() => undefined)

    expect(onSessionExpired).not.toHaveBeenCalled()
  })

  it('runs one expiration flow for concurrent 401 responses from the current token', async () => {
    const onSessionExpired = vi.fn()
    configureApiSession({ getToken: () => 'token-A', onSessionExpired })
    createFetchMock(() => jsonResponse({ title: 'Unauthorized' }, 401))

    await Promise.all([
      request('/protected').catch(() => undefined),
      request('/protected').catch(() => undefined),
    ])

    expect(onSessionExpired).toHaveBeenCalledTimes(1)
    expect(onSessionExpired).toHaveBeenCalledWith('token-A')
  })

  it('builds the stock structure query with trimmed optional filters', async () => {
    const fetchMock = createFetchMock(() => jsonResponse({}))
    const controller = new AbortController()

    await api.reports.stockStructure({ broker: ' 甲券商 ', instrumentType: 'Stock' }, { signal: controller.signal })

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/reports/stock-structure?broker=%E7%94%B2%E5%88%B8%E5%95%86&instrumentType=Stock',
      expect.objectContaining({ signal: controller.signal }),
    )
  })

  it('omits blank stock structure filters and serializes trend months', async () => {
    const fetchMock = createFetchMock(() => jsonResponse([]))

    await api.reports.stockStructure({ broker: '   ' })
    await api.reports.stockValueTrend({ months: 6 })

    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/reports/stock-structure')
    expect(fetchMock.mock.calls[1]?.[0]).toBe('/api/reports/stock-value-trend?months=6')
  })

  it('serializes market risk period and forwards the abort signal through the central request layer', async () => {
    const fetchMock = createFetchMock(() => jsonResponse({ periodMonths: 3 }))
    const controller = new AbortController()

    await api.reports.stockMarketRisk({ periodMonths: 3 }, { signal: controller.signal })

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/reports/stock-market-risk?periodMonths=3',
      expect.objectContaining({ signal: controller.signal }),
    )
  })
})
