import type {
  Category, Transaction, Installment, CreditCard, CreditCardBill,
  BankAccount, BankAccountListResponse, Stock, StockListResponse, Withdrawal, WithdrawalListResponse, PaymentMethod, PaginatedResponse,
  StockInstrumentType, StockMarket,
  StockMetadataUpdateRequest,
  TransactionListResponse, InstallmentListResponse,
  MonthlyTrend, CategoryDistribution, NetWorth, MonthlyForecast, MonthlySummary, DashboardSummary, NetWorthTrendPoint,
  StockStructureReport, StockValueTrendPoint, StockMarketRiskReport, StockPerformanceReport,
  StockTransactionListResponse, StockTransactionListItem, StockLedgerTransactionRequest,
  StockTransactionCostEstimateRequest, StockTransactionCostEstimateResponse,
  StockLedgerInitializationResponse, StockPositionRequest, StockPositionResponse,
  StockOption,
  SnapshotBatch, SnapshotListResponse, TrendPoint, SnapshotCompareResult, AutoSnapshotConfig,
  AuthResponse, TwoFactorSetupResponse, User, ApiToken, ExchangeRateResponse,
  SystemTimeZoneSettings, InstallmentCommandResponse, InstallmentPurchaseRequest, InstallmentPurchaseResponse,
  StandaloneInstallmentRequest, UpdateInstallmentScheduleRequest,
  ScheduleExecutionHistoryResponse, ScheduleExecutionQuery, ScheduleOverviewItem,
} from '../types'
import {
  ApiError,
  type ApiFieldErrors,
  type ApiTypedErrorDetails,
  RequestCancelledError,
  safeStatusMessage,
} from './errors.ts'
export { ApiError, RequestCancelledError, isRequestCancelled } from './errors.ts'

const BASE = '/api'

export interface ApiRequestContext {
  signal?: AbortSignal
}

export interface StockLookupResponse {
  name: string | null
  currentPrice: number | null
  market: StockMarket
  resultCode: string
}

// Adds an optional owner signal to an API request without changing existing callers.
function withRequestContext(options: RequestInit, context?: ApiRequestContext): RequestInit {
  return context?.signal ? { ...options, signal: context.signal } : options
}

export interface ApiSessionConfig {
  getToken: () => string | null
  onSessionExpired: (token: string) => void | Promise<void>
}

const defaultApiSession: ApiSessionConfig = {
  getToken: () => typeof localStorage === 'undefined' ? null : localStorage.getItem('authToken'),
  onSessionExpired: () => undefined,
}
let apiSession: ApiSessionConfig = defaultApiSession
let expiredToken: string | null = null
let expirationPromise: Promise<void> | null = null

// Configures the central session coordinator without coupling the API client to router or storage mutation.
export function configureApiSession(config: Partial<ApiSessionConfig>): void {
  apiSession = { ...apiSession, ...config }
  expiredToken = null
  expirationPromise = null
}

// Parses structured ProblemDetails fields into safe client-side error details.
function parseProblemDetails(body: unknown): {
  code: string | null
  title: string | null
  detail: string | null
  fieldErrors: ApiFieldErrors
  traceId: string | null
  details: ApiTypedErrorDetails | null
} {
  if (!body || typeof body !== 'object') {
    return { code: null, title: null, detail: null, fieldErrors: {}, traceId: null, details: null }
  }
  const record = body as Record<string, unknown>
  const errors = record.errors && typeof record.errors === 'object' ? record.errors as Record<string, unknown> : {}
  const fieldErrors = Object.fromEntries(
    Object.entries(errors).map(([key, value]) => [
      key,
      Array.isArray(value) ? value.filter(item => typeof item === 'string') as string[] : [String(value)],
    ]),
  )
  const rawDetails = record.details && typeof record.details === 'object' && !Array.isArray(record.details)
    ? record.details as Record<string, unknown>
    : null
  const typedDetails: ApiTypedErrorDetails | null = rawDetails
    ? {
        ...(typeof rawDetails.reason === 'string' ? { reason: rawDetails.reason } : {}),
        ...(typeof rawDetails.availableShares === 'number' && Number.isFinite(rawDetails.availableShares)
          ? { availableShares: rawDetails.availableShares }
          : {}),
        ...(typeof rawDetails.requestedShares === 'number' && Number.isFinite(rawDetails.requestedShares)
          ? { requestedShares: rawDetails.requestedShares }
          : {}),
        ...(typeof rawDetails.tradeDate === 'string' ? { tradeDate: rawDetails.tradeDate } : {}),
      }
    : null
  return {
    code: typeof record.code === 'string' ? record.code : null,
    title: typeof record.title === 'string' ? record.title : null,
    detail: typeof record.detail === 'string'
      ? record.detail
      : typeof record.message === 'string' ? record.message : null,
    fieldErrors,
    traceId: typeof record.traceId === 'string' ? record.traceId : null,
    details: typedDetails && Object.keys(typedDetails).length > 0 ? typedDetails : null,
  }
}

// Reads a response body once and returns JSON only when it is structurally parseable.
async function readResponseBody(response: Response): Promise<unknown> {
  const text = await response.text()
  if (!text.trim()) return null
  try {
    return JSON.parse(text) as unknown
  } catch {
    return null
  }
}

// Identifies auth endpoints that must not expire a session on their own 401 response.
function isPublicAuthRequest(url: string): boolean {
  return [
    '/auth/status',
    '/auth/register',
    '/auth/login',
    '/auth/2fa/login',
    '/auth/2fa/recovery-login',
    '/auth/logout',
  ].some(path => url.startsWith(path))
}

// Starts one session-expired transition for the token that initiated the failing request.
function expireCurrentSession(token: string | null): void {
  if (!token || token !== apiSession.getToken() || expiredToken === token) return
  expiredToken = token
  expirationPromise = Promise.resolve(apiSession.onSessionExpired(token)).finally(() => {
    expirationPromise = null
  })
  void expirationPromise
}

// Converts a browser abort or an already-aborted signal into the typed cancellation result.
function isAbortError(error: unknown, signal?: AbortSignal | null): boolean {
  return signal?.aborted === true || (error instanceof DOMException && error.name === 'AbortError')
}

// Performs one centrally controlled API request with cancellation and safe error semantics.
export async function request<T>(url: string, options?: RequestInit, acceptedStatuses: number[] = []): Promise<T> {
  const token = apiSession.getToken()
  const headers = new Headers(options?.headers)
  if (!headers.has('Content-Type')) headers.set('Content-Type', 'application/json')
  if (token) {
    headers.set('Authorization', `Bearer ${token}`)
  }

  let response: Response
  try {
    response = await fetch(`${BASE}${url}`, {
      ...options,
      headers,
    })
  } catch (error) {
    if (isAbortError(error, options?.signal)) throw new RequestCancelledError()
    throw new ApiError({
      status: null,
      title: 'Network error',
      detail: null,
      userMessage: '無法連線至伺服器，請稍後再試',
    })
  }

  if (response.status === 401 && !isPublicAuthRequest(url)) {
    expireCurrentSession(token)
  }
  if (!response.ok && !acceptedStatuses.includes(response.status)) {
    const body = await readResponseBody(response)
    const details = parseProblemDetails(body)
    throw new ApiError({
      status: response.status,
      code: details.code,
      title: details.title,
      detail: details.detail,
      fieldErrors: details.fieldErrors,
      traceId: details.traceId,
      details: details.details,
      userMessage: details.detail ?? details.title ?? safeStatusMessage(response.status),
    })
  }
  if (response.status === 204) return undefined as T
  try {
    return await response.json() as T
  } catch {
    throw new ApiError({
      status: response.status,
      title: 'Invalid response',
      detail: null,
      userMessage: '伺服器回應格式錯誤，請稍後再試',
    })
  }
}

// Builds the bank accounts list query string, omitting blank optional filters.
export function buildBankAccountsQuery(params?: { page?: number; pageSize?: number; bankName?: string }) {
  const q = new URLSearchParams()
  if (params?.page) q.set('page', String(params.page))
  if (params?.pageSize) q.set('pageSize', String(params.pageSize))
  const bankName = params?.bankName?.trim()
  if (bankName) q.set('bankName', bankName)
  return q.toString()
}

// Builds snapshot list/trend query strings, omitting blank optional date filters.
export function buildSnapshotQuery(params?: { page?: number; pageSize?: number; dateStart?: string; dateEnd?: string }) {
  const q = new URLSearchParams()
  if (params?.page) q.set('page', String(params.page))
  if (params?.pageSize) q.set('pageSize', String(params.pageSize))
  const dateStart = params?.dateStart?.trim()
  const dateEnd = params?.dateEnd?.trim()
  if (dateStart) q.set('dateStart', dateStart)
  if (dateEnd) q.set('dateEnd', dateEnd)
  return q.toString()
}

// Builds stock list query strings, omitting blank optional filters.
export function buildStocksQuery(params?: { page?: number; pageSize?: number; symbol?: string; broker?: string; includeClosed?: boolean }) {
  const q = new URLSearchParams()
  if (params?.page) q.set('page', String(params.page))
  if (params?.pageSize) q.set('pageSize', String(params.pageSize))
  const symbol = params?.symbol?.trim()
  const broker = params?.broker?.trim()
  if (symbol) q.set('symbol', symbol)
  if (broker) q.set('broker', broker)
  if (params?.includeClosed) q.set('includeClosed', 'true')
  return q.toString()
}

// 建立排程 execution 查詢字串並省略空白 optional filter。
export function buildScheduleExecutionsQuery(params?: ScheduleExecutionQuery): string {
  const q = new URLSearchParams()
  if (params?.jobKey?.trim()) q.set('jobKey', params.jobKey.trim())
  if (params?.status?.trim()) q.set('status', params.status.trim())
  if (params?.dateStart?.trim()) q.set('dateStart', params.dateStart.trim())
  if (params?.dateEnd?.trim()) q.set('dateEnd', params.dateEnd.trim())
  if (params?.page) q.set('page', String(params.page))
  if (params?.pageSize) q.set('pageSize', String(params.pageSize))
  return q.toString()
}

export const api = {
  categories: {
    list: (params?: { page?: number; pageSize?: number }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.page) q.set('page', String(params.page))
      if (params?.pageSize) q.set('pageSize', String(params.pageSize))
      return request<PaginatedResponse<Category>>(`/categories?${q}`, withRequestContext({}, context))
    },
    get: (id: number, context?: ApiRequestContext) => request<Category>(`/categories/${id}`, withRequestContext({}, context)),
    create: (data: Omit<Category, 'id'>, context?: ApiRequestContext) =>
      request<Category>('/categories', withRequestContext({ method: 'POST', body: JSON.stringify(data) }, context)),
    update: (id: number, data: Partial<Category>, context?: ApiRequestContext) =>
      request<Category>(`/categories/${id}`, withRequestContext({ method: 'PUT', body: JSON.stringify(data) }, context)),
    delete: (id: number, context?: ApiRequestContext) =>
      request<void>(`/categories/${id}`, withRequestContext({ method: 'DELETE' }, context)),
    restoreDefaults: (context?: ApiRequestContext) =>
      request<PaginatedResponse<Category>>('/categories/restore-defaults', withRequestContext({ method: 'POST' }, context)),
  },

  transactions: {
    list: (params?: { page?: number; pageSize?: number; categoryId?: number; startDate?: string; endDate?: string; search?: string; type?: string }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.page) q.set('page', String(params.page))
      if (params?.pageSize) q.set('pageSize', String(params.pageSize))
      if (params?.categoryId) q.set('categoryId', String(params.categoryId))
      if (params?.startDate) q.set('startDate', params.startDate)
      if (params?.endDate) q.set('endDate', params.endDate)
      if (params?.search) q.set('search', params.search)
      if (params?.type) q.set('type', params.type)
      return request<TransactionListResponse>(`/transactions?${q}`, withRequestContext({}, context))
    },
    get: (id: number, context?: ApiRequestContext) => request<Transaction>(`/transactions/${id}`, withRequestContext({}, context)),
    create: (data: Omit<Transaction, 'id' | 'createdAt' | 'category' | 'paymentMethod'>, context?: ApiRequestContext) =>
      request<Transaction>('/transactions', withRequestContext({ method: 'POST', body: JSON.stringify(data) }, context)),
    update: (id: number, data: Partial<Omit<Transaction, 'paymentMethod'>>, context?: ApiRequestContext) =>
      request<Transaction>(`/transactions/${id}`, withRequestContext({ method: 'PUT', body: JSON.stringify(data) }, context)),
    delete: (id: number, context?: ApiRequestContext) =>
      request<void>(`/transactions/${id}`, withRequestContext({ method: 'DELETE' }, context)),
  },

  installments: {
    list: (params?: { page?: number; pageSize?: number; cardId?: number; dateStart?: string; dateEnd?: string; status?: string }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.page) q.set('page', String(params.page))
      if (params?.pageSize) q.set('pageSize', String(params.pageSize))
      if (params?.cardId) q.set('cardId', String(params.cardId))
      if (params?.dateStart) q.set('dateStart', params.dateStart)
      if (params?.dateEnd) q.set('dateEnd', params.dateEnd)
      if (params?.status) q.set('status', params.status)
      return request<InstallmentListResponse>(`/installments?${q}`, withRequestContext({}, context))
    },
    get: (id: number, context?: ApiRequestContext) => request<Installment>(`/installments/${id}`, withRequestContext({}, context)),
    // Creates a standalone installment with a caller-provided idempotency key.
    create: (data: StandaloneInstallmentRequest, idempotencyKey: string, context?: ApiRequestContext) =>
      request<InstallmentCommandResponse>('/installments', withRequestContext({
        method: 'POST',
        headers: { 'Idempotency-Key': idempotencyKey },
        body: JSON.stringify(data),
      }, context)),
    // Updates schedule-affecting fields through the atomic schedule command.
    update: (id: number, data: UpdateInstallmentScheduleRequest, context?: ApiRequestContext) =>
      request<InstallmentCommandResponse>(`/installments/${id}`, withRequestContext({ method: 'PUT', body: JSON.stringify(data) }, context)),
    delete: (id: number, context?: ApiRequestContext) =>
      request<void>(`/installments/${id}`, withRequestContext({ method: 'DELETE' }, context)),
    // Sends an explicit payment target state instead of a toggle request.
    markPayment: (id: number, paymentId: number, data: { isPaid: boolean; paidDate?: string }, context?: ApiRequestContext) =>
      request<InstallmentCommandResponse>(`/installments/${id}/payments/${paymentId}`, withRequestContext({
        method: 'PATCH',
        body: JSON.stringify(data),
      }, context)),
  },

  installmentPurchases: {
    // Creates the transaction and its complete installment schedule atomically.
    create: (data: InstallmentPurchaseRequest, idempotencyKey: string, context?: ApiRequestContext) =>
      request<InstallmentPurchaseResponse>('/installment-purchases', withRequestContext({
        method: 'POST',
        headers: { 'Idempotency-Key': idempotencyKey },
        body: JSON.stringify(data),
      }, context)),
  },

  creditCards: {
    list: (params?: { page?: number; pageSize?: number }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.page) q.set('page', String(params.page))
      if (params?.pageSize) q.set('pageSize', String(params.pageSize))
      return request<PaginatedResponse<CreditCard>>(`/credit-cards?${q}`, withRequestContext({}, context))
    },
    get: (id: number, context?: ApiRequestContext) => request<CreditCard>(`/credit-cards/${id}`, withRequestContext({}, context)),
    create: (data: Omit<CreditCard, 'id' | 'createdAt' | 'updatedAt'>, context?: ApiRequestContext) =>
      request<CreditCard>('/credit-cards', withRequestContext({ method: 'POST', body: JSON.stringify(data) }, context)),
    update: (id: number, data: Partial<CreditCard>, context?: ApiRequestContext) =>
      request<CreditCard>(`/credit-cards/${id}`, withRequestContext({ method: 'PUT', body: JSON.stringify(data) }, context)),
    delete: (id: number, context?: ApiRequestContext) =>
      request<void>(`/credit-cards/${id}`, withRequestContext({ method: 'DELETE' }, context)),
  },

  creditCardBills: {
    list: (params?: { cardId?: number; isPaid?: boolean; dateStart?: string; dateEnd?: string }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.cardId) q.set('cardId', String(params.cardId))
      if (params?.isPaid !== undefined) q.set('isPaid', String(params.isPaid))
      if (params?.dateStart) q.set('dateStart', params.dateStart)
      if (params?.dateEnd) q.set('dateEnd', params.dateEnd)
      const qs = q.toString()
      return request<CreditCardBill[]>(`/credit-card-bills${qs ? `?${qs}` : ''}`, withRequestContext({}, context))
    },
    get: (id: number, context?: ApiRequestContext) => request<CreditCardBill>(`/credit-card-bills/${id}`, withRequestContext({}, context)),
  },

  bankAccounts: {
    list: (params?: { page?: number; pageSize?: number; bankName?: string }, context?: ApiRequestContext) => {
      const q = buildBankAccountsQuery(params)
      return request<BankAccountListResponse>(`/bank-accounts?${q}`, withRequestContext({}, context))
    },
    get: (id: number, context?: ApiRequestContext) => request<BankAccount>(`/bank-accounts/${id}`, withRequestContext({}, context)),
    create: (data: Omit<BankAccount, 'id' | 'createdAt' | 'updatedAt'>, context?: ApiRequestContext) =>
      request<BankAccount>('/bank-accounts', withRequestContext({ method: 'POST', body: JSON.stringify(data) }, context)),
    update: (id: number, data: Partial<BankAccount>, context?: ApiRequestContext) =>
      request<BankAccount>(`/bank-accounts/${id}`, withRequestContext({ method: 'PUT', body: JSON.stringify(data) }, context)),
    delete: (id: number, context?: ApiRequestContext) =>
      request<void>(`/bank-accounts/${id}`, withRequestContext({ method: 'DELETE' }, context)),
  },

  stocks: {
    list: (params?: { page?: number; pageSize?: number; symbol?: string; broker?: string; includeClosed?: boolean }, context?: ApiRequestContext) => {
      const q = buildStocksQuery(params)
      return request<StockListResponse>(`/stocks?${q}`, withRequestContext({}, context))
    },
    // 讀取不受持股分頁限制的完整交易股票 options。
    options: (params?: { includeClosed?: boolean }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.includeClosed) q.set('includeClosed', 'true')
      const qs = q.toString()
      return request<StockOption[]>(`/stocks/options${qs ? `?${qs}` : ''}`, withRequestContext({}, context))
    },
    get: (id: number, context?: ApiRequestContext) => request<Stock>(`/stocks/${id}`, withRequestContext({}, context)),
    update: (id: number, data: StockMetadataUpdateRequest, context?: ApiRequestContext) =>
      request<Stock>(`/stocks/${id}`, withRequestContext({ method: 'PUT', body: JSON.stringify(data) }, context)),
    delete: (id: number, context?: ApiRequestContext) =>
      request<void>(`/stocks/${id}`, withRequestContext({ method: 'DELETE' }, context)),
    lookup: (symbol: string, context?: ApiRequestContext) =>
      request<StockLookupResponse>(`/stocks/lookup?symbol=${encodeURIComponent(symbol)}`, withRequestContext({}, context)),
    ledger: {
      // 依股票 Ledger filter、日期及分頁讀取固定排序的交易列表。
      list: (params?: { stockId?: number; type?: string; dateStart?: string; dateEnd?: string; page?: number; pageSize?: number }, context?: ApiRequestContext) => {
        const q = new URLSearchParams()
        if (params?.stockId) q.set('stockId', String(params.stockId))
        if (params?.type) q.set('type', params.type)
        if (params?.dateStart) q.set('dateStart', params.dateStart)
        if (params?.dateEnd) q.set('dateEnd', params.dateEnd)
        if (params?.page) q.set('page', String(params.page))
        if (params?.pageSize) q.set('pageSize', String(params.pageSize))
        const qs = q.toString()
        return request<StockTransactionListResponse>(`/stocks/ledger${qs ? `?${qs}` : ''}`, withRequestContext({}, context))
      },
      // 讀取單筆交易及其 replay 衍生欄位。
      get: (id: number, context?: ApiRequestContext) =>
        request<StockTransactionListItem>(`/stocks/ledger/${id}`, withRequestContext({}, context)),
      // 建立 Buy、Sell 或 Dividend 交易。
      create: (data: StockLedgerTransactionRequest, context?: ApiRequestContext) =>
        request<StockTransactionListItem>('/stocks/ledger/transactions', withRequestContext({ method: 'POST', body: JSON.stringify(data) }, context)),
      // 修改既有交易並由 backend 完整 replay。
      update: (id: number, data: StockLedgerTransactionRequest, context?: ApiRequestContext) =>
        request<StockTransactionListItem>(`/stocks/ledger/transactions/${id}`, withRequestContext({ method: 'PUT', body: JSON.stringify(data) }, context)),
      // 刪除交易並由 backend 驗證剩餘歷史。
      delete: (id: number, context?: ApiRequestContext) =>
        request<void>(`/stocks/ledger/transactions/${id}`, withRequestContext({ method: 'DELETE' }, context)),
      // 透過 backend 既有估值規則讀取單筆買賣交易的預估費稅。
      estimateCosts: (data: StockTransactionCostEstimateRequest, context?: ApiRequestContext) =>
        request<StockTransactionCostEstimateResponse>('/stocks/ledger/estimate-costs', withRequestContext({
          method: 'POST',
          body: JSON.stringify(data),
        }, context)),
      // 以使用者選定的 baseline date 初始化既有持股。
      initialize: (data: { baselineDate: string }, context?: ApiRequestContext) =>
        request<StockLedgerInitializationResponse>('/stocks/ledger/initialize', withRequestContext({ method: 'POST', body: JSON.stringify(data) }, context), [422]),
    },
    positions: {
      // 以單一 request 原子建立 Stock 與第一筆 Buy 或 OpeningBalance。
      create: (data: StockPositionRequest, context?: ApiRequestContext) =>
        request<StockPositionResponse>('/stocks/positions', withRequestContext({ method: 'POST', body: JSON.stringify(data) }, context)),
    },
  },

  withdrawals: {
    list: (params?: { page?: number; pageSize?: number; startDate?: string; endDate?: string }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.page) q.set('page', String(params.page))
      if (params?.pageSize) q.set('pageSize', String(params.pageSize))
      if (params?.startDate) q.set('startDate', params.startDate)
      if (params?.endDate) q.set('endDate', params.endDate)
      return request<WithdrawalListResponse>(`/withdrawals?${q}`, withRequestContext({}, context))
    },
    get: (id: number, context?: ApiRequestContext) => request<Withdrawal>(`/withdrawals/${id}`, withRequestContext({}, context)),
    create: (data: Omit<Withdrawal, 'id' | 'bankAccount'>, context?: ApiRequestContext) =>
      request<Withdrawal>('/withdrawals', withRequestContext({ method: 'POST', body: JSON.stringify(data) }, context)),
    update: (id: number, data: Partial<Withdrawal>, context?: ApiRequestContext) =>
      request<Withdrawal>(`/withdrawals/${id}`, withRequestContext({ method: 'PUT', body: JSON.stringify(data) }, context)),
    delete: (id: number, context?: ApiRequestContext) =>
      request<void>(`/withdrawals/${id}`, withRequestContext({ method: 'DELETE' }, context)),
  },

  paymentMethods: {
    list: (params?: { page?: number; pageSize?: number }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.page) q.set('page', String(params.page))
      if (params?.pageSize) q.set('pageSize', String(params.pageSize))
      return request<PaginatedResponse<PaymentMethod>>(`/payment-methods?${q}`, withRequestContext({}, context))
    },
    get: (id: number, context?: ApiRequestContext) => request<PaymentMethod>(`/payment-methods/${id}`, withRequestContext({}, context)),
    create: (data: Omit<PaymentMethod, 'id'>, context?: ApiRequestContext) =>
      request<PaymentMethod>('/payment-methods', withRequestContext({ method: 'POST', body: JSON.stringify(data) }, context)),
    update: (id: number, data: Partial<PaymentMethod>, context?: ApiRequestContext) =>
      request<PaymentMethod>(`/payment-methods/${id}`, withRequestContext({ method: 'PUT', body: JSON.stringify(data) }, context)),
    delete: (id: number, context?: ApiRequestContext) =>
      request<void>(`/payment-methods/${id}`, withRequestContext({ method: 'DELETE' }, context)),
    restoreDefaults: (context?: ApiRequestContext) =>
      request<PaginatedResponse<PaymentMethod>>('/payment-methods/restore-defaults', withRequestContext({ method: 'POST' }, context)),
  },

  reports: {
    incomeExpenseTrend: (params?: { dateStart?: string; dateEnd?: string }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.dateStart) q.set('dateStart', params.dateStart)
      if (params?.dateEnd) q.set('dateEnd', params.dateEnd)
      const qs = q.toString()
      return request<MonthlyTrend[]>(`/reports/income-expense-trend${qs ? `?${qs}` : ''}`, withRequestContext({}, context))
    },
    categoryDistribution: (params?: { dateStart?: string; dateEnd?: string }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.dateStart) q.set('dateStart', params.dateStart)
      if (params?.dateEnd) q.set('dateEnd', params.dateEnd)
      const qs = q.toString()
      return request<CategoryDistribution[]>(`/reports/category-distribution${qs ? `?${qs}` : ''}`, withRequestContext({}, context))
    },
    netWorth: (context?: ApiRequestContext) => request<NetWorth>('/reports/net-worth', withRequestContext({}, context)),
    installmentForecast: (params?: { months?: number }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.months) q.set('months', String(params.months))
      const qs = q.toString()
      return request<MonthlyForecast[]>(`/reports/installment-forecast${qs ? `?${qs}` : ''}`, withRequestContext({}, context))
    },
    monthlySummary: (params?: { year?: number; month?: number }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.year) q.set('year', String(params.year))
      if (params?.month) q.set('month', String(params.month))
      const qs = q.toString()
      return request<MonthlySummary>(`/reports/monthly-summary${qs ? `?${qs}` : ''}`, withRequestContext({}, context))
    },
    dashboardSummary: (params?: { year?: number; month?: number }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.year) q.set('year', String(params.year))
      if (params?.month) q.set('month', String(params.month))
      const qs = q.toString()
      return request<DashboardSummary>(`/reports/dashboard-summary${qs ? `?${qs}` : ''}`, withRequestContext({}, context))
    },
    netWorthTrend: (params?: { months?: number }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.months) q.set('months', String(params.months))
      const qs = q.toString()
      return request<NetWorthTrendPoint[]>(`/reports/net-worth-trend${qs ? `?${qs}` : ''}`, withRequestContext({}, context))
    },
    // 取得目前篩選範圍的持股結構報表。
    stockStructure: (params?: { broker?: string; instrumentType?: StockInstrumentType }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.broker?.trim()) q.set('broker', params.broker.trim())
      if (params?.instrumentType) q.set('instrumentType', params.instrumentType)
      const qs = q.toString()
      return request<StockStructureReport>(`/reports/stock-structure${qs ? `?${qs}` : ''}`, withRequestContext({}, context))
    },
    // 取得全部持股的實際快照價值趨勢。
    stockValueTrend: (params?: { months?: number }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.months) q.set('months', String(params.months))
      const qs = q.toString()
      return request<StockValueTrendPoint[]>(`/reports/stock-value-trend${qs ? `?${qs}` : ''}`, withRequestContext({}, context))
    },
    // 讀取只依賴本機行情的市場風險情境報表。
    stockMarketRisk: (params?: { periodMonths?: 3 | 6 | 12 }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.periodMonths) q.set('periodMonths', String(params.periodMonths))
      const qs = q.toString()
      return request<StockMarketRiskReport>(`/reports/stock-market-risk${qs ? `?${qs}` : ''}`, withRequestContext({}, context))
    },
    // 讀取本機 Ledger、目前估值與 raw Close 組合的股票投資績效。
    stockPerformance: (params?: { dateStart?: string; dateEnd?: string }, context?: ApiRequestContext) => {
      const q = new URLSearchParams()
      if (params?.dateStart) q.set('dateStart', params.dateStart)
      if (params?.dateEnd) q.set('dateEnd', params.dateEnd)
      const qs = q.toString()
      return request<StockPerformanceReport>(`/reports/stock-performance${qs ? `?${qs}` : ''}`, withRequestContext({}, context))
    },
  },

  auth: {
    status: (context?: ApiRequestContext) => request<{ authenticated: boolean; user: User | null; hasUsers: boolean }>('/auth/status', withRequestContext({}, context)),
    // 以 dedicated header 傳送首次初始化密鑰，且不將密鑰寫入瀏覽器儲存。
    register: (data: { email: string; displayName: string; password: string }, bootstrapSecret: string, context?: ApiRequestContext) =>
      request<AuthResponse>('/auth/register', withRequestContext({
        method: 'POST',
        body: JSON.stringify(data),
        headers: { 'X-MyExpenses-Bootstrap-Secret': bootstrapSecret },
      }, context)),
    login: (data: { email: string; password: string }, context?: ApiRequestContext) =>
      request<AuthResponse>('/auth/login', withRequestContext({ method: 'POST', body: JSON.stringify(data) }, context)),
    verify2fa: (data: { tempToken: string; code: string }, context?: ApiRequestContext) =>
      request<AuthResponse>('/auth/2fa/login', withRequestContext({ method: 'POST', body: JSON.stringify(data) }, context)),
    recoveryLogin: (data: { tempToken: string; recoveryCode: string }, context?: ApiRequestContext) =>
      request<AuthResponse>('/auth/2fa/recovery-login', withRequestContext({ method: 'POST', body: JSON.stringify(data) }, context)),
    setup2fa: (context?: ApiRequestContext) =>
      request<TwoFactorSetupResponse>('/auth/2fa/setup', withRequestContext({ method: 'POST' }, context)),
    verify2faSetup: (data: { code: string }, context?: ApiRequestContext) =>
      request<{ enabled: boolean; recoveryCodes: string[] }>('/auth/2fa/verify', withRequestContext({ method: 'POST', body: JSON.stringify(data) }, context)),
    disable2fa: (context?: ApiRequestContext) =>
      request<{ disabled: boolean }>('/auth/2fa/disable', withRequestContext({ method: 'POST' }, context)),
    updateProfile: (data: { displayName: string }, context?: ApiRequestContext) =>
      request<User>('/auth/profile', withRequestContext({ method: 'PUT', body: JSON.stringify(data) }, context)),
    changePassword: (data: { currentPassword: string; newPassword: string }, context?: ApiRequestContext) =>
      request<{ message: string }>('/auth/password', withRequestContext({ method: 'PUT', body: JSON.stringify(data) }, context)),
    getRecoveryCodes: (context?: ApiRequestContext) =>
      request<{ recoveryCodes: string[] }>('/auth/2fa/recovery-codes', withRequestContext({}, context)),
    logout: (context?: ApiRequestContext) =>
      request<{ message: string }>('/auth/logout', withRequestContext({ method: 'POST' }, context)),
    logoutAll: (context?: ApiRequestContext) =>
      request<{ message: string }>('/auth/logout-all', withRequestContext({ method: 'POST' }, context)),
  },
  settings: {
    getTimeZone: (context?: ApiRequestContext) => request<SystemTimeZoneSettings>('/settings/timezone', withRequestContext({}, context)),
    updateTimeZone: (timeZoneId: string, context?: ApiRequestContext) =>
      request<SystemTimeZoneSettings>('/settings/timezone', withRequestContext({
        method: 'PUT',
        body: JSON.stringify({ timeZoneId }),
      }, context)),
  },

  snapshots: {
    list: (params?: { page?: number; pageSize?: number; dateStart?: string; dateEnd?: string }, context?: ApiRequestContext) => {
      const q = buildSnapshotQuery(params)
      return request<SnapshotListResponse>(`/snapshots${q ? `?${q}` : ''}`, withRequestContext({}, context))
    },
    get: (id: number, context?: ApiRequestContext) => request<SnapshotBatch>(`/snapshots/${id}`, withRequestContext({}, context)),
    create: (context?: ApiRequestContext) => request<SnapshotBatch>('/snapshots', withRequestContext({ method: 'POST' }, context)),
    delete: (id: number, context?: ApiRequestContext) => request<void>(`/snapshots/${id}`, withRequestContext({ method: 'DELETE' }, context)),
    compare: (id1: number, id2: number, context?: ApiRequestContext) =>
      request<SnapshotCompareResult>(`/snapshots/${id1}/compare/${id2}`, withRequestContext({}, context)),
    trend: (params?: { dateStart?: string; dateEnd?: string }, context?: ApiRequestContext) => {
      const q = buildSnapshotQuery(params)
      return request<TrendPoint[]>(`/snapshots/trend${q ? `?${q}` : ''}`, withRequestContext({}, context))
    },
    getSchedule: (context?: ApiRequestContext) => request<AutoSnapshotConfig>('/snapshots/auto-schedule', withRequestContext({}, context)),
    updateSchedule: (data: Partial<AutoSnapshotConfig>, context?: ApiRequestContext) =>
      request<AutoSnapshotConfig>('/snapshots/auto-schedule', withRequestContext({ method: 'PUT', body: JSON.stringify(data) }, context)),
  },
  schedules: {
    // 讀取後端計算的三個業務排程總覽。
    overview: (context?: ApiRequestContext) =>
      request<ScheduleOverviewItem[]>('/schedules', withRequestContext({}, context)),
    // 讀取依排程、狀態與系統本地日期篩選的 execution 歷史。
    executions: (params?: ScheduleExecutionQuery, context?: ApiRequestContext) => {
      const query = buildScheduleExecutionsQuery(params)
      return request<ScheduleExecutionHistoryResponse>(
        `/schedules/executions${query ? `?${query}` : ''}`,
        withRequestContext({}, context),
      )
    },
  },
  exchangeRates: {
    get: (context?: ApiRequestContext) => request<ExchangeRateResponse>('/exchange-rates', withRequestContext({}, context)),
  },
  apiTokens: {
    list: (context?: ApiRequestContext): Promise<ApiToken[]> => request('/auth/api-tokens', withRequestContext({}, context)),
    create: (name: string, scopes: string[], context?: ApiRequestContext): Promise<{ id: number; name: string; prefix: string; createdAt: string; scopes: string[] | null; token: string }> =>
      request('/auth/api-tokens', withRequestContext({ method: 'POST', body: JSON.stringify({ name, scopes }) }, context)),
    revoke: (id: number, context?: ApiRequestContext): Promise<void> =>
      request(`/auth/api-tokens/${id}`, withRequestContext({ method: 'DELETE' }, context)),
  },
}
