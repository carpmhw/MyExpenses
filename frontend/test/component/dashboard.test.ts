import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import Dashboard from '../../src/pages/dashboard/index.vue'
import { api } from '../../src/api'
import { createTestRouter } from '../support/render'
import { deferred } from '../support/deferred'

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
}

describe('Dashboard reliability states', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('keeps successful sections usable when one independent query fails', async () => {
    const summaryRequest = deferred<typeof summary>()
    vi.spyOn(api.reports, 'dashboardSummary').mockReturnValue(summaryRequest.promise)
    vi.spyOn(api.withdrawals, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, summary: { totalAmount: 0, count: 0, averageAmount: 0, maxAmount: 0 } })
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
    vi.spyOn(api.withdrawals, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 50, summary: { totalAmount: 0, count: 0, averageAmount: 0, maxAmount: 0 } })
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

    expect(wrapper.text()).toContain('NT$ 0')
    expect(wrapper.text()).not.toContain('載入失敗')
  })
})
