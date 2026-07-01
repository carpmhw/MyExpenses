import type {
  Category, Transaction, Installment, CreditCard, CreditCardBill,
  BankAccount, BankAccountListResponse, Stock, StockListResponse, Withdrawal, PaymentMethod, PaginatedResponse, InstallmentPayment,
  MonthlyTrend, CategoryDistribution, NetWorth, MonthlyForecast, MonthlySummary,
  SnapshotBatch, TrendPoint, SnapshotCompareResult, AutoSnapshotConfig,
  AuthResponse, TwoFactorSetupResponse, User, ApiToken, ExchangeRateResponse,
} from '../types'

const BASE = '/api'

function getAuthToken(): string | null {
  return localStorage.getItem('authToken')
}

async function request<T>(url: string, options?: RequestInit): Promise<T> {
  const token = getAuthToken()
  const headers: Record<string, string> = { 'Content-Type': 'application/json' }
  if (token) {
    headers['Authorization'] = `Bearer ${token}`
  }

  const res = await fetch(`${BASE}${url}`, {
    headers,
    ...options,
  })
  if (res.status === 401 && !url.startsWith('/auth/')) {
    localStorage.removeItem('authToken')
    window.location.href = '/login'
    throw new Error('Unauthorized')
  }
  if (!res.ok) {
    const text = await res.text()
    throw new Error(text || `HTTP ${res.status}`)
  }
  if (res.status === 204) return undefined as T
  return res.json()
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
export function buildStocksQuery(params?: { page?: number; pageSize?: number; symbol?: string; broker?: string }) {
  const q = new URLSearchParams()
  if (params?.page) q.set('page', String(params.page))
  if (params?.pageSize) q.set('pageSize', String(params.pageSize))
  const symbol = params?.symbol?.trim()
  const broker = params?.broker?.trim()
  if (symbol) q.set('symbol', symbol)
  if (broker) q.set('broker', broker)
  return q.toString()
}

export const api = {
  categories: {
    list: (params?: { page?: number; pageSize?: number }) => {
      const q = new URLSearchParams()
      if (params?.page) q.set('page', String(params.page))
      if (params?.pageSize) q.set('pageSize', String(params.pageSize))
      return request<PaginatedResponse<Category>>(`/categories?${q}`)
    },
    get: (id: number) => request<Category>(`/categories/${id}`),
    create: (data: Omit<Category, 'id'>) =>
      request<Category>('/categories', { method: 'POST', body: JSON.stringify(data) }),
    update: (id: number, data: Partial<Category>) =>
      request<Category>(`/categories/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: number) =>
      request<void>(`/categories/${id}`, { method: 'DELETE' }),
    restoreDefaults: () =>
      request<PaginatedResponse<Category>>('/categories/restore-defaults', { method: 'POST' }),
  },

  transactions: {
    list: (params?: { page?: number; pageSize?: number; categoryId?: number; startDate?: string; endDate?: string; search?: string; type?: string }) => {
      const q = new URLSearchParams()
      if (params?.page) q.set('page', String(params.page))
      if (params?.pageSize) q.set('pageSize', String(params.pageSize))
      if (params?.categoryId) q.set('categoryId', String(params.categoryId))
      if (params?.startDate) q.set('startDate', params.startDate)
      if (params?.endDate) q.set('endDate', params.endDate)
      if (params?.search) q.set('search', params.search)
      if (params?.type) q.set('type', params.type)
      return request<PaginatedResponse<Transaction>>(`/transactions?${q}`)
    },
    get: (id: number) => request<Transaction>(`/transactions/${id}`),
    create: (data: Omit<Transaction, 'id' | 'createdAt' | 'category' | 'paymentMethod'>) =>
      request<Transaction>('/transactions', { method: 'POST', body: JSON.stringify(data) }),
    update: (id: number, data: Partial<Omit<Transaction, 'paymentMethod'>>) =>
      request<Transaction>(`/transactions/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: number) =>
      request<void>(`/transactions/${id}`, { method: 'DELETE' }),
  },

  installments: {
    list: (params?: { page?: number; pageSize?: number; cardId?: number; dateStart?: string; dateEnd?: string; status?: string }) => {
      const q = new URLSearchParams()
      if (params?.page) q.set('page', String(params.page))
      if (params?.pageSize) q.set('pageSize', String(params.pageSize))
      if (params?.cardId) q.set('cardId', String(params.cardId))
      if (params?.dateStart) q.set('dateStart', params.dateStart)
      if (params?.dateEnd) q.set('dateEnd', params.dateEnd)
      if (params?.status) q.set('status', params.status)
      return request<PaginatedResponse<Installment>>(`/installments?${q}`)
    },
    get: (id: number) => request<Installment>(`/installments/${id}`),
    create: (data: Omit<Installment, 'id' | 'createdAt' | 'transaction' | 'card' | 'payments' | 'remainingPeriods' | 'status'>) =>
      request<Installment>('/installments', { method: 'POST', body: JSON.stringify(data) }),
    update: (id: number, data: Partial<Installment>) =>
      request<Installment>(`/installments/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: number) =>
      request<void>(`/installments/${id}`, { method: 'DELETE' }),
    markPayment: (id: number, paymentId: number, paidDate?: string) =>
      request<InstallmentPayment>(`/installments/${id}/payments/${paymentId}`, {
        method: 'PATCH',
        body: JSON.stringify(paidDate ? { paidDate } : {}),
      }),
  },

  creditCards: {
    list: (params?: { page?: number; pageSize?: number }) => {
      const q = new URLSearchParams()
      if (params?.page) q.set('page', String(params.page))
      if (params?.pageSize) q.set('pageSize', String(params.pageSize))
      return request<PaginatedResponse<CreditCard>>(`/credit-cards?${q}`)
    },
    get: (id: number) => request<CreditCard>(`/credit-cards/${id}`),
    create: (data: Omit<CreditCard, 'id' | 'createdAt' | 'updatedAt'>) =>
      request<CreditCard>('/credit-cards', { method: 'POST', body: JSON.stringify(data) }),
    update: (id: number, data: Partial<CreditCard>) =>
      request<CreditCard>(`/credit-cards/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: number) =>
      request<void>(`/credit-cards/${id}`, { method: 'DELETE' }),
  },

  creditCardBills: {
    list: (params?: { cardId?: number; isPaid?: boolean; dateStart?: string; dateEnd?: string }) => {
      const q = new URLSearchParams()
      if (params?.cardId) q.set('cardId', String(params.cardId))
      if (params?.isPaid !== undefined) q.set('isPaid', String(params.isPaid))
      if (params?.dateStart) q.set('dateStart', params.dateStart)
      if (params?.dateEnd) q.set('dateEnd', params.dateEnd)
      const qs = q.toString()
      return request<CreditCardBill[]>(`/credit-card-bills${qs ? `?${qs}` : ''}`)
    },
    get: (id: number) => request<CreditCardBill>(`/credit-card-bills/${id}`),
  },

  bankAccounts: {
    list: (params?: { page?: number; pageSize?: number; bankName?: string }) => {
      const q = buildBankAccountsQuery(params)
      return request<BankAccountListResponse>(`/bank-accounts?${q}`)
    },
    get: (id: number) => request<BankAccount>(`/bank-accounts/${id}`),
    create: (data: Omit<BankAccount, 'id' | 'createdAt' | 'updatedAt'>) =>
      request<BankAccount>('/bank-accounts', { method: 'POST', body: JSON.stringify(data) }),
    update: (id: number, data: Partial<BankAccount>) =>
      request<BankAccount>(`/bank-accounts/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: number) =>
      request<void>(`/bank-accounts/${id}`, { method: 'DELETE' }),
  },

  stocks: {
    list: (params?: { page?: number; pageSize?: number; symbol?: string; broker?: string }) => {
      const q = buildStocksQuery(params)
      return request<StockListResponse>(`/stocks?${q}`)
    },
    get: (id: number) => request<Stock>(`/stocks/${id}`),
    create: (data: Omit<Stock, 'id'>) =>
      request<Stock>('/stocks', { method: 'POST', body: JSON.stringify(data) }),
    update: (id: number, data: Partial<Stock>) =>
      request<Stock>(`/stocks/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: number) =>
      request<void>(`/stocks/${id}`, { method: 'DELETE' }),
    lookup: (symbol: string) =>
      request<{ name: string | null; currentPrice: number | null }>(`/stocks/lookup?symbol=${encodeURIComponent(symbol)}`),
  },

  withdrawals: {
    list: (params?: { page?: number; pageSize?: number; startDate?: string; endDate?: string }) => {
      const q = new URLSearchParams()
      if (params?.page) q.set('page', String(params.page))
      if (params?.pageSize) q.set('pageSize', String(params.pageSize))
      if (params?.startDate) q.set('startDate', params.startDate)
      if (params?.endDate) q.set('endDate', params.endDate)
      return request<PaginatedResponse<Withdrawal>>(`/withdrawals?${q}`)
    },
    get: (id: number) => request<Withdrawal>(`/withdrawals/${id}`),
    create: (data: Omit<Withdrawal, 'id' | 'bankAccount'>) =>
      request<Withdrawal>('/withdrawals', { method: 'POST', body: JSON.stringify(data) }),
    update: (id: number, data: Partial<Withdrawal>) =>
      request<Withdrawal>(`/withdrawals/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: number) =>
      request<void>(`/withdrawals/${id}`, { method: 'DELETE' }),
  },

  paymentMethods: {
    list: (params?: { page?: number; pageSize?: number }) => {
      const q = new URLSearchParams()
      if (params?.page) q.set('page', String(params.page))
      if (params?.pageSize) q.set('pageSize', String(params.pageSize))
      return request<PaginatedResponse<PaymentMethod>>(`/payment-methods?${q}`)
    },
    get: (id: number) => request<PaymentMethod>(`/payment-methods/${id}`),
    create: (data: Omit<PaymentMethod, 'id'>) =>
      request<PaymentMethod>('/payment-methods', { method: 'POST', body: JSON.stringify(data) }),
    update: (id: number, data: Partial<PaymentMethod>) =>
      request<PaymentMethod>(`/payment-methods/${id}`, { method: 'PUT', body: JSON.stringify(data) }),
    delete: (id: number) =>
      request<void>(`/payment-methods/${id}`, { method: 'DELETE' }),
    restoreDefaults: () =>
      request<PaginatedResponse<PaymentMethod>>('/payment-methods/restore-defaults', { method: 'POST' }),
  },

  reports: {
    incomeExpenseTrend: (params?: { dateStart?: string; dateEnd?: string }) => {
      const q = new URLSearchParams()
      if (params?.dateStart) q.set('dateStart', params.dateStart)
      if (params?.dateEnd) q.set('dateEnd', params.dateEnd)
      const qs = q.toString()
      return request<MonthlyTrend[]>(`/reports/income-expense-trend${qs ? `?${qs}` : ''}`)
    },
    categoryDistribution: (params?: { dateStart?: string; dateEnd?: string }) => {
      const q = new URLSearchParams()
      if (params?.dateStart) q.set('dateStart', params.dateStart)
      if (params?.dateEnd) q.set('dateEnd', params.dateEnd)
      const qs = q.toString()
      return request<CategoryDistribution[]>(`/reports/category-distribution${qs ? `?${qs}` : ''}`)
    },
    netWorth: () => request<NetWorth>('/reports/net-worth'),
    installmentForecast: (params?: { months?: number }) => {
      const q = new URLSearchParams()
      if (params?.months) q.set('months', String(params.months))
      const qs = q.toString()
      return request<MonthlyForecast[]>(`/reports/installment-forecast${qs ? `?${qs}` : ''}`)
    },
    monthlySummary: (params?: { year?: number; month?: number }) => {
      const q = new URLSearchParams()
      if (params?.year) q.set('year', String(params.year))
      if (params?.month) q.set('month', String(params.month))
      const qs = q.toString()
      return request<MonthlySummary>(`/reports/monthly-summary${qs ? `?${qs}` : ''}`)
    },
  },

  auth: {
    status: () => request<{ authenticated: boolean; user: User | null; hasUsers: boolean }>('/auth/status'),
    register: (data: { email: string; displayName: string; password: string }) =>
      request<AuthResponse>('/auth/register', { method: 'POST', body: JSON.stringify(data) }),
    login: (data: { email: string; password: string }) =>
      request<AuthResponse>('/auth/login', { method: 'POST', body: JSON.stringify(data) }),
    verify2fa: (data: { tempToken: string; code: string }) =>
      request<AuthResponse>('/auth/2fa/login', { method: 'POST', body: JSON.stringify(data) }),
    recoveryLogin: (data: { tempToken: string; recoveryCode: string }) =>
      request<AuthResponse>('/auth/2fa/recovery-login', { method: 'POST', body: JSON.stringify(data) }),
    setup2fa: () =>
      request<TwoFactorSetupResponse>('/auth/2fa/setup', { method: 'POST' }),
    verify2faSetup: (data: { code: string }) =>
      request<{ enabled: boolean; recoveryCodes: string[] }>('/auth/2fa/verify', { method: 'POST', body: JSON.stringify(data) }),
    disable2fa: () =>
      request<{ disabled: boolean }>('/auth/2fa/disable', { method: 'POST' }),
    updateProfile: (data: { displayName: string }) =>
      request<User>('/auth/profile', { method: 'PUT', body: JSON.stringify(data) }),
    changePassword: (data: { currentPassword: string; newPassword: string }) =>
      request<{ message: string }>('/auth/password', { method: 'PUT', body: JSON.stringify(data) }),
    getRecoveryCodes: () =>
      request<{ recoveryCodes: string[] }>('/auth/2fa/recovery-codes'),
    logoutAll: () =>
      request<{ message: string }>('/auth/logout-all', { method: 'POST' }),
  },

  snapshots: {
    list: (params?: { page?: number; pageSize?: number; dateStart?: string; dateEnd?: string }) => {
      const q = buildSnapshotQuery(params)
      return request<PaginatedResponse<SnapshotBatch>>(`/snapshots${q ? `?${q}` : ''}`)
    },
    get: (id: number) => request<SnapshotBatch>(`/snapshots/${id}`),
    create: () => request<SnapshotBatch>('/snapshots', { method: 'POST' }),
    delete: (id: number) => request<void>(`/snapshots/${id}`, { method: 'DELETE' }),
    compare: (id1: number, id2: number) =>
      request<SnapshotCompareResult>(`/snapshots/${id1}/compare/${id2}`),
    trend: (params?: { dateStart?: string; dateEnd?: string }) => {
      const q = buildSnapshotQuery(params)
      return request<TrendPoint[]>(`/snapshots/trend${q ? `?${q}` : ''}`)
    },
    getSchedule: () => request<AutoSnapshotConfig>('/snapshots/auto-schedule'),
    updateSchedule: (data: Partial<AutoSnapshotConfig>) =>
      request<AutoSnapshotConfig>('/snapshots/auto-schedule', { method: 'PUT', body: JSON.stringify(data) }),
  },
  exchangeRates: {
    get: () => request<ExchangeRateResponse>('/exchange-rates'),
  },
  apiTokens: {
    list: (): Promise<ApiToken[]> => request('/auth/api-tokens'),
    create: (name: string, scopes: string[]): Promise<{ id: number; name: string; prefix: string; createdAt: string; scopes: string[] | null; token: string }> =>
      request('/auth/api-tokens', { method: 'POST', body: JSON.stringify({ name, scopes }) }),
    revoke: (id: number): Promise<void> =>
      request(`/auth/api-tokens/${id}`, { method: 'DELETE' }),
  },
}
