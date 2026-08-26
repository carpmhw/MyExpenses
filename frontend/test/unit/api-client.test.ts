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

  // 驗證 Ledger typed error 的 code 與安全 message 不會被 central client 丟失。
  it('parses typed ledger errors into a safe ApiError', async () => {
    createFetchMock(() => jsonResponse({
      code: 'InsufficientShares',
      message: '賣出股數超過可用股數',
      details: { availableShares: 2, requestedShares: 5 },
    }, 409))

    const error = await request('/stocks/ledger/transactions').catch(value => value)

    expect(error).toMatchObject({
      status: 409,
      code: 'InsufficientShares',
      detail: '賣出股數超過可用股數',
      userMessage: '賣出股數超過可用股數',
    })
  })

  // 驗證 Ledger 初始化的 typed blocking response 即使是 422 仍可被 UI 讀取。
  it('returns typed initialization blocking data from a 422 response', async () => {
    createFetchMock(() => jsonResponse({
      initializedCount: 0,
      skippedCount: 1,
      blockingCount: 1,
      totalCount: 2,
      blockingStocks: [{ stockId: 2, symbol: '2330', reason: 'MissingBuyPrice', code: 'MissingBuyPrice', buyPrice: 0, currentPrice: 600 }],
    }, 422))

    await expect(api.stocks.ledger.initialize({ baselineDate: '2026-08-01' })).resolves.toMatchObject({
      blockingCount: 1,
      blockingStocks: [{ symbol: '2330', code: 'MissingBuyPrice' }],
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

  // 驗證股票績效期間參數與 request signal 透過中央 API client 傳送。
  it('serializes stock performance period and forwards the abort signal', async () => {
    const fetchMock = createFetchMock(() => jsonResponse({}))
    const controller = new AbortController()

    await api.reports.stockPerformance(
      { dateStart: '2026-01-01', dateEnd: '2026-12-31' },
      { signal: controller.signal },
    )

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/reports/stock-performance?dateStart=2026-01-01&dateEnd=2026-12-31',
      expect.objectContaining({ signal: controller.signal }),
    )
  })

  // 驗證 Ledger filter、交易 mutation、初始化與 atomic position 都使用指定 API contract。
  it('serializes stock ledger queries and atomic position commands', async () => {
    const fetchMock = createFetchMock(() => jsonResponse({}))

    await api.stocks.ledger.list({
      stockId: 3,
      type: 'Sell',
      dateStart: '2026-01-01',
      dateEnd: '2026-01-31',
      page: 2,
      pageSize: 10,
    })
    await api.stocks.ledger.create({
      stockId: 3,
      type: 'Dividend',
      tradeDate: '2026-01-15',
      cashAmount: 100,
      fee: 1,
      tax: 2,
    })
    await api.stocks.ledger.initialize({ baselineDate: '2026-01-01' })
    await api.stocks.positions.create({
      name: '測試標的',
      symbol: '2330',
      market: 'Twse',
      instrumentType: 'Stock',
      shares: 10,
      buyPrice: 100,
      currentPrice: 110,
      tradeDate: '2026-01-01',
      initialTransactionType: 'Buy',
    })

    expect(fetchMock.mock.calls[0]?.[0]).toBe('/api/stocks/ledger?stockId=3&type=Sell&dateStart=2026-01-01&dateEnd=2026-01-31&page=2&pageSize=10')
    expect(fetchMock.mock.calls[1]?.[0]).toBe('/api/stocks/ledger/transactions')
    expect(fetchMock.mock.calls[2]?.[0]).toBe('/api/stocks/ledger/initialize')
    expect(fetchMock.mock.calls[3]?.[0]).toBe('/api/stocks/positions')
    expect(JSON.parse(String(fetchMock.mock.calls[2]?.[1]?.body))).toEqual({ baselineDate: '2026-01-01' })
    expect(JSON.parse(String(fetchMock.mock.calls[3]?.[1]?.body))).toMatchObject({ initialTransactionType: 'Buy' })
  })

  // 驗證交易 selector 使用不受 holdings 分頁限制的完整 Stock Options API。
  it('serializes stock options queries with includeClosed', async () => {
    const fetchMock = createFetchMock(() => jsonResponse([]))
    type StockOptionsApi = {
      options: (params?: { includeClosed?: boolean }) => Promise<unknown>
    }

    await (api.stocks as unknown as StockOptionsApi).options({ includeClosed: true })

    expect(fetchMock).toHaveBeenCalledWith('/api/stocks/options?includeClosed=true', expect.anything())
  })

  // 驗證股票更新 client 只傳送 metadata contract 欄位。
  it('serializes restricted stock metadata updates', async () => {
    const fetchMock = createFetchMock(() => jsonResponse({ id: 3 }))

    await api.stocks.update(3, {
      name: '台積電',
      market: 'Twse',
      currentPrice: 650,
      lastPriceUpdate: '2026-08-25T00:00:00Z',
    })

    expect(fetchMock).toHaveBeenCalledWith('/api/stocks/3', expect.objectContaining({
      method: 'PUT',
      body: JSON.stringify({
        name: '台積電',
        market: 'Twse',
        currentPrice: 650,
        lastPriceUpdate: '2026-08-25T00:00:00Z',
      }),
    }))
  })
})
