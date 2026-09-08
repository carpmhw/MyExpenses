import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import Dashboard from '../../src/pages/dashboard/index.vue'
import { api } from '../../src/api'
import { createTestRouter } from '../support/render'
import { deferred } from '../support/deferred'
import type { Installment, Transaction, Withdrawal } from '../../src/types'
import { formatMoney } from '../../src/utils/format'

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
  amount: 15000,
  date: '2026-08-01',
  description: '薪資',
  bankAccountId: 1,
  bankAccount: {
    id: 1,
    bankName: '測試銀行',
    accountNumber: '1234',
    accountType: '活期',
    balance: 15000,
    currencyCode: 'TWD',
    createdAt: '2026-08-01T00:00:00Z',
    updatedAt: '2026-08-01T00:00:00Z',
  },
}

const activityExpense: Transaction = {
  id: 2,
  type: 'Expense',
  amount: 12649,
  date: '2026-08-02',
  description: '一筆非常長的支出摘要用於驗證描述可以截斷但金額必須完整顯示',
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
  totalAmount: 1855,
  periods: 1,
  perPeriod: 1855,
  remainingPeriods: 1,
  status: 'Active',
  purchaseDate: '2026-08-03',
  createdAt: '2026-08-03T00:00:00Z',
  description: '信用卡長摘要量測：這是一段足以觸發摘要截斷的非常長文字',
  transaction: null,
  card: null,
  payments: [{ id: 31, installmentId: 3, period: 1, amount: 1855, paidDate: null, dueDate: '2026-08-23', isPaid: false }],
}

const emptyDescriptionInstallment: Installment = {
  id: 4,
  transactionId: null,
  cardId: 1,
  totalAmount: 0,
  periods: 1,
  perPeriod: 0,
  remainingPeriods: 1,
  status: 'Active',
  purchaseDate: '2026-08-04',
  createdAt: '2026-08-04T00:00:00Z',
  description: null,
  transaction: null,
  card: null,
  payments: [{ id: 41, installmentId: 4, period: 1, amount: 0, paidDate: null, dueDate: null, isPaid: false }],
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
      expect(header.classes()).not.toContain('xl:max-2xl:flex-col')
      expect(header.classes()).not.toContain('xl:max-2xl:items-stretch')
      expect(header.classes()).not.toContain('xl:max-2xl:min-h-[148px]')
      expect(header.element.children[0].classList.contains('flex-1')).toBe(true)
      expect(header.element.children[0].classList.contains('min-w-0')).toBe(true)
      expect(header.element.children[0].classList.contains('flex-none')).toBe(false)
      expect(header.element.children[1].classList.contains('shrink-0')).toBe(true)
      expect(header.element.children[1].classList.contains('text-right')).toBe(true)
      expect(header.element.children[1].classList.contains('max-sm:self-end')).toBe(true)
      expect(header.element.children[1].classList.contains('min-w-0')).toBe(false)
      expect(header.element.children[1].classList.contains('max-w-[35%]')).toBe(false)
      expect(header.element.children[1].classList.contains('max-sm:max-w-full')).toBe(false)
      const headerAmount = header.findAll('p').find(element => element.attributes('title'))
      expect(headerAmount?.classes()).toContain('whitespace-nowrap')
      expect(headerAmount?.classes()).toContain('text-xl')
      expect(headerAmount?.classes()).toContain('leading-7')
      expect(headerAmount?.classes()).toContain('font-bold')
      expect(headerAmount?.classes()).toContain('tabular-nums')
      expect(headerAmount?.classes()).not.toContain('text-2xl')
      expect(headerAmount?.classes()).not.toContain('truncate')
    }

    const creditHeader = headers[2]
    const creditTitle = creditHeader.findAll('p').find(element => element.text() === '信用卡交易')
    const creditSubtitle = creditHeader.findAll('p').find(element => element.text() === 'Credit Card Transactions')
    const creditDueLabel = creditHeader.findAll('p').find(element => element.text() === '本期應繳')
    const creditDueAmount = creditHeader.findAll('p').find(element => element.text() === '$1,855.00')
    expect(creditTitle?.classes()).toContain('whitespace-nowrap')
    expect(creditSubtitle?.classes()).toContain('whitespace-nowrap')
    expect(creditDueLabel).toBeDefined()
    expect(creditDueLabel?.classes()).toContain('mt-1')
    expect(creditDueAmount?.classes()).toContain('whitespace-nowrap')
    expect(creditDueAmount?.classes()).toContain('tabular-nums')
    expect(creditDueAmount?.classes()).not.toContain('truncate')
    expect(creditDueAmount?.attributes('title')).toBe('$1,855.00')
    expect(creditHeader.classes()).toContain('max-sm:flex-col')
    expect(creditHeader.classes()).toContain('max-sm:items-stretch')
    expect((creditHeader.element.children[1] as HTMLElement).classList.contains('shrink-0')).toBe(true)
    expect((creditHeader.element.children[1] as HTMLElement).classList.contains('max-sm:self-end')).toBe(true)
    expect(creditHeader.text()).toContain('$1,855.00')

    const creditTableHeader = cards[2].findAll('div').find(element =>
      element.classes().includes('uppercase') && element.text().includes('項目 / 摘要'))
    const creditGrid = 'grid-cols-[44px_minmax(0,1fr)_80px_112px_48px_80px]'
    expect(creditTableHeader?.classes()).toContain('grid')
    expect(creditTableHeader?.classes()).toContain(creditGrid)
    expect(creditTableHeader?.classes()).toContain('gap-2')
    expect(creditTableHeader?.classes()).toContain('px-5')
    expect(creditTableHeader?.classes()).toContain('max-sm:flex')
    expect(creditTableHeader?.classes()).toContain('max-sm:flex-wrap')
    expect(creditTableHeader?.classes()).not.toContain('xl:max-2xl:flex-wrap')
    expect(creditTableHeader?.classes()).not.toContain('xl:max-2xl:grid-cols-[32px_minmax(0,1fr)_56px_96px_40px_56px]')
    const creditSummaryHeader = creditTableHeader?.findAll('span').find(element => element.text() === '項目 / 摘要')
    expect(creditSummaryHeader?.classes()).toContain('whitespace-nowrap')
    expect(creditSummaryHeader?.classes()).toContain('max-sm:order-last')
    expect(creditSummaryHeader?.classes()).toContain('max-sm:basis-full')
    expect(creditTableHeader?.findAll('span').map(element => element.text())).toEqual(['日期', '項目 / 摘要', '總額', '期數', '已繳', '本期'])

    const creditRow = cards[2].find('div.cursor-pointer')
    expect(creditRow.classes()).toContain('grid')
    expect(creditRow.classes()).toContain(creditGrid)
    expect(creditRow.classes()).toContain('gap-2')
    expect(creditRow.classes()).toContain('px-5')
    expect(creditRow.classes()).toContain('max-sm:flex')
    expect(creditRow.classes()).toContain('max-sm:flex-wrap')
    expect(creditRow.classes()).toContain('max-sm:gap-0.5')
    expect(creditRow.classes()).not.toContain('xl:max-2xl:flex-wrap')
    expect(creditRow.classes()).not.toContain('xl:max-2xl:grid-cols-[32px_minmax(0,1fr)_56px_96px_40px_56px]')
    const responsiveCreditClasses = [
      'sm:max-md:grid-cols-[44px_minmax(0,1fr)_96px_112px_48px_120px]',
      'sm:max-md:gap-1',
      'md:grid-cols-[44px_minmax(0,1fr)_96px_112px_48px_120px]',
      'xl:max-2xl:grid-cols-[32px_minmax(0,1fr)_68px_88px_32px_72px]',
      'xl:max-2xl:gap-px',
      'xl:max-2xl:px-2',
      '2xl:grid-cols-[44px_minmax(0,1fr)_96px_112px_48px_120px]',
      '2xl:gap-0.5',
      '2xl:px-2',
    ]
    for (const className of responsiveCreditClasses) {
      expect(creditTableHeader?.classes()).toContain(className)
      expect(creditRow.classes()).toContain(className)
    }
    expect(creditRow.classes()).toEqual(expect.arrayContaining(
      creditTableHeader?.classes().filter(className => ['grid', creditGrid, 'gap-2', 'px-5'].includes(className)) ?? [],
    ))
    const creditSummary = creditRow.findAll('div').find(element => element.classes().includes('min-w-0'))
    expect(creditSummary?.classes()).toContain('max-sm:order-last')
    expect(creditSummary?.classes()).toContain('max-sm:basis-full')
    expect(creditSummary?.find('p').classes()).toContain('truncate')
    expect(creditSummary?.find('p').attributes('title')).toBe(activityInstallment.description)
    const expectedCreditAmount = formatMoney(activityInstallment.totalAmount)
    const creditAmountCells = creditRow.findAll('span').filter(element => element.attributes('title') === expectedCreditAmount)
    expect(creditAmountCells).toHaveLength(2)
    for (const amountCell of creditAmountCells) {
      expect(amountCell.classes()).toContain('whitespace-nowrap')
      expect(amountCell.classes()).toContain('tabular-nums')
      expect(amountCell.classes()).not.toContain('truncate')
    }
    const creditDate = creditRow.findAll('span').find(element => element.text() === '08/03')
    expect(creditDate?.classes()).toContain('whitespace-nowrap')
    expect(creditDate?.classes()).toContain('tabular-nums')
    expect(creditDate?.classes()).not.toContain('truncate')
    const creditPeriod = creditRow.findAll('span').find(element => element.text() === '1 期（一次付清）')
    expect(creditPeriod).toBeDefined()
    const creditProgress = creditRow.findAll('span').find(element => element.text() === '0/1')
    expect(creditProgress?.classes()).toContain('whitespace-nowrap')
    expect(creditProgress?.classes()).toContain('tabular-nums')

    expect(headers[0].text()).toContain('$15,000.00')
    expect(headers[1].text()).toContain('$12,649.00')
    expect(headers[2].text()).toContain('$1,855.00')
    const expenseHeaderAmount = headers[1].findAll('p').find(element => element.text() === '$12,649.00')
    expect(expenseHeaderAmount?.classes()).toContain('whitespace-nowrap')
    expect(expenseHeaderAmount?.classes()).toContain('tabular-nums')
    expect(expenseHeaderAmount?.classes()).not.toContain('truncate')
    expect(expenseHeaderAmount?.attributes('title')).toBe('$12,649.00')
    const expenseRow = cards[1].find('div.cursor-pointer')
    const expenseDescription = expenseRow.findAll('p').find(element => element.text() === activityExpense.description)
    expect(expenseDescription?.classes()).toContain('truncate')
    const expenseAmount = expenseRow.findAll('span').find(element => element.text() === formatMoney(activityExpense.amount))
    expect(expenseAmount?.classes()).toContain('shrink-0')
    expect(expenseAmount?.classes()).toContain('w-32')
    expect(expenseAmount?.classes()).toContain('whitespace-nowrap')
    expect(expenseAmount?.classes()).toContain('tabular-nums')
    expect(expenseAmount?.classes()).not.toContain('truncate')
  })

  // 驗證零值、空描述與摘要尚未取得時，Dashboard 仍顯示明確替代文字。
  it('keeps zero and unavailable summary values explicit with an empty description', async () => {
    const summaryRequest = deferred<typeof summary>()
    vi.spyOn(api.reports, 'dashboardSummary').mockReturnValue(summaryRequest.promise)
    vi.spyOn(api.withdrawals, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, summary: { totalAmount: 0, count: 0, averageAmount: 0, maxAmount: 0, baseCurrency: 'TWD', exchangeRateUpdatedAt: null, exchangeRateIsStale: false, conversionAvailable: true, totalAmountInBaseCurrency: 0 } })
    vi.spyOn(api.transactions, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, summary: { totalAmount: 0, totalIncome: 0, totalExpense: 0, count: 0, dailyAverage: 0, maxAmount: 0 } })
    vi.spyOn(api.installments, 'list').mockResolvedValue({ items: [emptyDescriptionInstallment], total: 1, page: 1, pageSize: 50, summary: { totalCount: 1, activeCount: 1, dueAmount: 0, duePaymentCount: 1 } })
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

    expect(wrapper.text()).toContain('不可用')
    summaryRequest.resolve(summary)
    await flushPromises()

    expect(wrapper.text()).toContain('$0.00')
    expect(wrapper.text()).toContain('NT$ 0')
    expect(wrapper.text()).toContain('—')
    const emptyDescription = wrapper.findAll('p').find(element => element.text() === '—' && element.attributes('title') === '—')
    expect(emptyDescription?.classes()).toContain('truncate')
    expect(emptyDescription?.attributes('title')).toBe('—')
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

    await cards[2].find('div.cursor-pointer').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/installments')

    await cards[2].find('button').trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.path).toBe('/installments')
  })
})
