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
})
