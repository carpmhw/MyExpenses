import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import Dashboard from '../../src/pages/dashboard/index.vue'
import { api } from '../../src/api'
import { createTestRouter } from '../support/render'
import { deferred } from '../support/deferred'
import type { Installment, Transaction, Withdrawal } from '../../src/types'

// 等待 Vue watcher 與非同步查詢完成目前排程。
async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await new Promise(resolve => setTimeout(resolve, 0))
}

const summary = {
  totalWithdrawals: 0,
  withdrawalCount: 0,
  totalExpenses: 0,
  expenseCount: 0,
  disposableBalance: 0,
  installmentDueAmount: 0,
  installmentDuePaymentCount: 0,
  activeInstallmentCount: 0,
  previousDisposableBalance: 0,
  baseCurrency: 'TWD' as const,
  exchangeRateUpdatedAt: null,
  exchangeRateIsStale: false,
  conversionAvailable: true,
}

const activityWithdrawal: Withdrawal = {
  id: 1,
  amount: 10000,
  date: '2026-08-01',
  description: '薪資',
  bankAccountId: 1,
  bankAccount: {
    id: 1,
    bankName: '測試銀行',
    accountNumber: '1234',
    accountType: '活期',
    balance: 10000,
    currencyCode: 'TWD',
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:00:00Z',
  },
}

const activityExpense: Transaction = {
  id: 2,
  type: 'Expense',
  amount: 5000,
  date: '2026-08-02',
  description: '一筆長摘要用於測試卡片內容空間',
  notes: null,
  categoryId: 1,
  paymentMethodId: null,
  createdAt: '2026-08-02T00:00:00Z',
  category: { id: 1, name: '餐飲', type: 'Expense', icon: '', color: '', sortOrder: 1 },
  paymentMethod: null,
}

const activityInstallment: Installment = {
  id: 3,
  transactionId: null,
  cardId: 1,
  totalAmount: 1200,
  periods: 1,
  perPeriod: 1200,
  remainingPeriods: 1,
  status: 'Active',
  purchaseDate: '2026-08-03',
  createdAt: '2026-08-03T00:00:00Z',
  description: '信用卡長摘要量測',
  transaction: null,
  card: null,
  payments: [],
}

// 建立活動卡片測試所需的成功 API 回應，讓三張卡片都渲染實際內容。
function mockDashboardActivityData(): void {
  vi.spyOn(api.reports, 'dashboardSummary').mockResolvedValue({
    ...summary,
    totalWithdrawals: activityWithdrawal.amount,
    withdrawalCount: 1,
    totalExpenses: activityExpense.amount,
    expenseCount: 1,
    disposableBalance: activityWithdrawal.amount - activityExpense.amount,
    installmentDueAmount: activityInstallment.perPeriod,
    installmentDuePaymentCount: 1,
    activeInstallmentCount: 1,
  })
  vi.spyOn(api.withdrawals, 'list').mockResolvedValue({
    items: [activityWithdrawal],
    total: 1,
    page: 1,
    pageSize: 50,
    summary: {
      totalAmount: activityWithdrawal.amount,
      count: 1,
      averageAmount: activityWithdrawal.amount,
      maxAmount: activityWithdrawal.amount,
      baseCurrency: 'TWD',
      exchangeRateUpdatedAt: null,
      exchangeRateIsStale: false,
      conversionAvailable: true,
      totalAmountInBaseCurrency: activityWithdrawal.amount,
    },
  })
  vi.spyOn(api.transactions, 'list').mockResolvedValue({
    items: [activityExpense],
    total: 1,
    page: 1,
    pageSize: 50,
    summary: {
      totalAmount: activityExpense.amount,
      totalIncome: 0,
      totalExpense: activityExpense.amount,
      count: 1,
      dailyAverage: activityExpense.amount,
      maxAmount: activityExpense.amount,
    },
  })
  vi.spyOn(api.installments, 'list').mockResolvedValue({
    items: [activityInstallment],
    total: 1,
    page: 1,
    pageSize: 50,
    summary: { totalCount: 1, activeCount: 1, dueAmount: activityInstallment.perPeriod, duePaymentCount: 1 },
  })
}

describe('Dashboard reliability states', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('keeps successful sections usable when one independent query fails', async () => {
    const summaryRequest = deferred<typeof summary>()
    vi.spyOn(api.reports, 'dashboardSummary').mockReturnValue(summaryRequest.promise)
    vi.spyOn(api.withdrawals, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, summary: { totalAmount: 0, count: 0, averageAmount: 0, maxAmount: 0, baseCurrency: 'TWD', exchangeRateUpdatedAt: null, exchangeRateIsStale: false, conversionAvailable: true, totalAmountInBaseCurrency: 0 } })
    vi.spyOn(api.transactions, 'list').mockResolvedValue({
      items: [{ id: 1, type: 'Expense', amount: 10, date: '2026-08-01', description: '午餐', notes: null, categoryId: 1, paymentMethodId: null, createdAt: '2026-08-01T00:00:00Z', category: { id: 1, name: '餐飲', type: 'Expense', icon: '', color: '', sortOrder: 1 }, paymentMethod: null }],
      total: 1,
      page: 1,
      pageSize: 50,
      summary: { totalAmount: 10, totalIncome: 0, totalExpense: 10, count: 1, dailyAverage: 10, maxAmount: 10 },
    })
    vi.spyOn(api.installments, 'list').mockRejectedValue(new Error('installment unavailable'))
    const router = createTestRouter()
    const wrapper = mount(Dashboard, {
      global: {
        plugins: [router],
        provide: {
          toast: { error: vi.fn() },
          timeZone: { timeZoneId: { value: 'Asia/Taipei' }, isReady: { value: true }, loadError: { value: false }, getToday: () => '2026-08-02', formatDateTime: (value: string) => value },
        },
      },
    })

    summaryRequest.resolve(summary)
    await flushPromises()

    expect(wrapper.text()).toContain('午餐')
    expect(wrapper.text()).toContain('載入失敗')
    expect(wrapper.text()).not.toContain('載入儀表板資料失敗')
  })

  it('renders a true zero summary as success instead of treating it as failure', async () => {
    vi.spyOn(api.reports, 'dashboardSummary').mockResolvedValue(summary)
    vi.spyOn(api.withdrawals, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, summary: { totalAmount: 0, count: 0, averageAmount: 0, maxAmount: 0, baseCurrency: 'TWD', exchangeRateUpdatedAt: null, exchangeRateIsStale: false, conversionAvailable: true, totalAmountInBaseCurrency: 0 } })
    vi.spyOn(api.transactions, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, summary: { totalAmount: 0, totalIncome: 0, totalExpense: 0, count: 0, dailyAverage: 0, maxAmount: 0 } })
    vi.spyOn(api.installments, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, summary: { totalCount: 0, activeCount: 0, dueAmount: 0, duePaymentCount: 0 } })
    const router = createTestRouter()
    const wrapper = mount(Dashboard, {
      global: {
        plugins: [router],
        provide: {
          toast: { error: vi.fn() },
          timeZone: { timeZoneId: { value: 'Asia/Taipei' }, isReady: { value: true }, loadError: { value: false }, getToday: () => '2026-08-02', formatDateTime: (value: string) => value },
        },
      },
    })

    await flushPromises()

    expect(wrapper.text()).toContain('$0.00')
    expect(wrapper.text()).not.toContain('載入失敗')
  })

  it('formats summary in TWD, keeps recent withdrawals in original currency, and warns for stale rates', async () => {
    vi.spyOn(api.reports, 'dashboardSummary').mockResolvedValue({
      ...summary,
      totalWithdrawals: 10000,
      disposableBalance: 10000,
      exchangeRateUpdatedAt: '2026-08-01T00:00:00Z',
      exchangeRateIsStale: true,
    })
    vi.spyOn(api.withdrawals, 'list').mockResolvedValue({
      items: [{
        id: 1,
        amount: 310,
        date: '2026-08-01',
        description: null,
        bankAccountId: 1,
        bankAccount: {
          id: 1,
          bankName: '美元銀行',
          accountNumber: '12345',
          accountType: '活期',
          balance: 0,
          currencyCode: 'USD',
          createdAt: '2026-08-01T00:00:00Z',
          updatedAt: '2026-08-01T00:00:00Z',
        },
      }],
      total: 1,
      page: 1,
      pageSize: 50,
      summary: {
        totalAmount: 10000,
        count: 1,
        averageAmount: 10000,
        maxAmount: 10000,
        baseCurrency: 'TWD',
        exchangeRateUpdatedAt: '2026-08-01T00:00:00Z',
        exchangeRateIsStale: true,
        conversionAvailable: true,
        totalAmountInBaseCurrency: 10000,
      },
    })
    vi.spyOn(api.transactions, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, summary: { totalAmount: 0, totalIncome: 0, totalExpense: 0, count: 0, dailyAverage: 0, maxAmount: 0 } })
    vi.spyOn(api.installments, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, summary: { totalCount: 0, activeCount: 0, dueAmount: 0, duePaymentCount: 0 } })
    const router = createTestRouter()
    const wrapper = mount(Dashboard, {
      global: {
        plugins: [router],
        provide: {
          toast: { error: vi.fn() },
          timeZone: { timeZoneId: { value: 'Asia/Taipei' }, isReady: { value: true }, loadError: { value: false }, getToday: () => '2026-08-02', formatDateTime: (value: string) => value },
        },
      },
    })

    await flushPromises()

    expect(wrapper.text()).toContain('$10,000.00')
    expect(wrapper.text()).toContain('US$310.00')
    expect(wrapper.text()).toContain('此摘要使用過期匯率')
    expect(wrapper.text()).toContain('2026/08/01 08:00')
  })

  it('uses credit card transaction wording and displays one-period activity', async () => {
    const onePeriod: Installment = {
      id: 7,
      transactionId: null,
      cardId: 3,
      totalAmount: 1200,
      periods: 1,
      perPeriod: 1200,
      remainingPeriods: 1,
      status: 'Active',
      purchaseDate: '2026-08-02',
      createdAt: '2026-08-20T00:00:00Z',
      description: '一次付清測試',
      transaction: null,
      card: null,
      payments: [{ id: 71, installmentId: 7, period: 1, amount: 1200, paidDate: null, dueDate: '2026-08-23', isPaid: false }],
    }
    vi.spyOn(api.reports, 'dashboardSummary').mockResolvedValue({ ...summary, installmentDueAmount: 1200, installmentDuePaymentCount: 1, activeInstallmentCount: 1 })
    vi.spyOn(api.withdrawals, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, summary: { totalAmount: 0, count: 0, averageAmount: 0, maxAmount: 0, baseCurrency: 'TWD', exchangeRateUpdatedAt: null, exchangeRateIsStale: false, conversionAvailable: true, totalAmountInBaseCurrency: 0 } })
    vi.spyOn(api.transactions, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, summary: { totalAmount: 0, totalIncome: 0, totalExpense: 0, count: 0, dailyAverage: 0, maxAmount: 0 } })
    vi.spyOn(api.installments, 'list').mockResolvedValue({ items: [onePeriod] })
    const router = createTestRouter()
    const wrapper = mount(Dashboard, {
      global: {
        plugins: [router],
        provide: {
          timeZone: { timeZoneId: { value: 'Asia/Taipei' }, isReady: { value: true }, loadError: { value: false }, getToday: () => '2026-08-02', formatDateTime: (value: string) => value },
        },
      },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('本期信用卡應繳')
    expect(wrapper.text()).toContain('信用卡交易')
    expect(wrapper.text()).toContain('Credit Card Transactions')
    expect(wrapper.text()).toContain('1 期（一次付清）')
    expect(wrapper.text()).toContain('08/02')
    expect(wrapper.text()).not.toContain('08/20')
    expect(wrapper.text()).not.toContain('信用卡分期')
  })

  // 驗證活動卡片使用新的桌面欄寬與共同 Header 高度契約。
  it('rebalances activity card columns and aligns all headers', async () => {
    mockDashboardActivityData()
    const router = createTestRouter()
    const wrapper = mount(Dashboard, {
      global: {
        plugins: [router],
        provide: {
          timeZone: { timeZoneId: { value: 'Asia/Taipei' }, isReady: { value: true }, loadError: { value: false }, getToday: () => '2026-08-02', formatDateTime: (value: string) => value },
        },
      },
    })
    await flushPromises()

    const cards = wrapper.findAll('[data-testid="dashboard-activity-card"]')
    expect(cards).toHaveLength(3)
    const gridClassName = cards[0].element.parentElement?.className ?? ''
    expect(gridClassName).toContain('grid-cols-1')
    expect(gridClassName).toContain('xl:grid-cols-[340px_minmax(0,0.8fr)_minmax(0,1.2fr)]')

    const headers = cards.map(card => card.find('div[class*="bg-gradient-to-br"]'))
    for (const header of headers) {
      expect(header.classes()).toContain('min-h-[104px]')
      expect(header.classes()).toContain('px-5')
      expect(header.classes()).toContain('py-4')
      expect(header.classes()).toContain('max-sm:flex-col')
      expect(header.classes()).toContain('max-sm:items-stretch')
      expect(header.classes()).toContain('max-sm:min-h-[148px]')
      expect(header.classes()).toContain('xl:max-2xl:flex-col')
      expect(header.classes()).toContain('xl:max-2xl:items-stretch')
      expect(header.classes()).toContain('xl:max-2xl:min-h-[148px]')
    }

    const creditHeader = headers[2]
    const creditTitle = creditHeader.findAll('p').find(element => element.text() === '信用卡交易')
    const creditSubtitle = creditHeader.findAll('p').find(element => element.text() === 'Credit Card Transactions')
    expect(creditTitle?.classes()).toContain('whitespace-nowrap')
    expect(creditSubtitle?.classes()).toContain('whitespace-nowrap')
    expect(creditHeader.classes()).toContain('max-sm:flex-col')
    expect(creditHeader.classes()).toContain('max-sm:items-stretch')
    expect((creditHeader.element.children[1] as HTMLElement).classList.contains('shrink-0')).toBe(true)
    expect((creditHeader.element.children[1] as HTMLElement).classList.contains('max-sm:self-end')).toBe(true)

    const creditTableHeader = cards[2].findAll('div').find(element =>
      element.classes().includes('uppercase') && element.text().includes('項目 / 摘要'))
    expect(creditTableHeader?.classes()).toContain('max-sm:flex-wrap')
    expect(creditTableHeader?.classes()).toContain('xl:max-2xl:flex-wrap')
    const creditSummaryHeader = creditTableHeader?.findAll('span').find(element => element.text() === '項目 / 摘要')
    expect(creditSummaryHeader?.classes()).toContain('max-sm:order-last')
    expect(creditSummaryHeader?.classes()).toContain('max-sm:basis-full')

    const creditRow = cards[2].find('div.cursor-pointer')
    expect(creditRow.classes()).toContain('max-sm:flex-wrap')
    expect(creditRow.classes()).toContain('xl:max-2xl:flex-wrap')
    const creditSummary = creditRow.findAll('div').find(element => element.classes().includes('flex-1'))
    expect(creditSummary?.classes()).toContain('max-sm:order-last')
    expect(creditSummary?.classes()).toContain('max-sm:basis-full')
  })

  // 驗證三張活動卡片的既有資料列導覽目標不受版面調整影響。
  it('preserves activity card navigation targets', async () => {
    mockDashboardActivityData()
    const router = createTestRouter([
      { path: '/dashboard', component: { template: '<div />' } },
      { path: '/withdrawals', component: { template: '<div />' } },
      { path: '/transactions', component: { template: '<div />' } },
      { path: '/installments', component: { template: '<div />' } },
    ])
    const wrapper = mount(Dashboard, {
      global: {
        plugins: [router],
        provide: {
          timeZone: { timeZoneId: { value: 'Asia/Taipei' }, isReady: { value: true }, loadError: { value: false }, getToday: () => '2026-08-02', formatDateTime: (value: string) => value },
        },
      },
    })
    await flushPromises()
    const cards = wrapper.findAll('[data-testid="dashboard-activity-card"]')

    await cards[0].find('div.cursor-pointer').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/withdrawals')

    await cards[1].find('div.cursor-pointer').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/transactions')

    await cards[2].find('button').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/installments')
  })
})
