import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../src/api'
import ExpensesPage from '../../src/pages/expenses/index.vue'
import InstallmentsPage from '../../src/pages/installments/index.vue'
import { createTestRouter } from '../support/render'
import { deferred } from '../support/deferred'

async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

const transactionResponse = {
  items: [],
  total: 0,
  page: 1,
  pageSize: 15,
  summary: { totalAmount: 0, totalIncome: 0, totalExpense: 0, count: 0, dailyAverage: 0, maxAmount: 0 },
}

const installment = {
  id: 7,
  transactionId: null,
  cardId: 3,
  totalAmount: 300,
  periods: 3,
  perPeriod: 100,
  remainingPeriods: 3,
  status: 'Active' as const,
  purchaseDate: '2026-08-01',
  createdAt: '2026-08-01T00:00:00Z',
  description: '測試分期',
  transaction: null,
  card: { id: 3, bankName: '測試銀行', lastFourDigits: '1234', cardNetwork: null, statementDay: 15, dueDay: 23, creditLimit: 10000, notes: null, createdAt: '', updatedAt: '' },
  payments: [{ id: 71, installmentId: 7, period: 1, amount: 100, paidDate: null, dueDate: '2026-08-23', isPaid: false }],
}

describe('financial page query identities', () => {
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('debounces transaction search into one resulting query transition', async () => {
    vi.useFakeTimers()
    const list = vi.spyOn(api.transactions, 'list').mockResolvedValue(transactionResponse)
    vi.spyOn(api.categories, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 999 })
    vi.spyOn(api.paymentMethods, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 999 })
    vi.spyOn(api.creditCards, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 999 })
    const wrapper = mount(ExpensesPage, { global: { plugins: [createTestRouter()], provide: { toast: { success: vi.fn(), error: vi.fn() } } } })
    await vi.runAllTimersAsync()
    await flushPromises()
    expect(list).toHaveBeenCalledTimes(1)

    await wrapper.get('input[placeholder="搜尋項目或備註..."]').setValue('午餐')
    vi.advanceTimersByTime(299)
    await flushPromises()
    expect(list).toHaveBeenCalledTimes(1)
    vi.advanceTimersByTime(1)
    await vi.runAllTimersAsync()
    await flushPromises()

    expect(list).toHaveBeenCalledTimes(2)
    expect(list.mock.calls[1][0]).toMatchObject({ search: '午餐', page: 1 })
  })

  it('loads installment schedule by selected resource identity', async () => {
    vi.spyOn(api.installments, 'list').mockResolvedValue({ items: [installment], total: 1, page: 1, pageSize: 15, summary: { totalCount: 1, activeCount: 1, dueAmount: 100, duePaymentCount: 1 } })
    const get = vi.spyOn(api.installments, 'get').mockResolvedValue(installment)
    vi.spyOn(api.creditCardBills, 'list').mockResolvedValue([])
    vi.spyOn(api.creditCards, 'list').mockResolvedValue({ items: [installment.card], total: 1, page: 1, pageSize: 999 })
    const wrapper = mount(InstallmentsPage, { global: { plugins: [createTestRouter()], provide: { toast: { success: vi.fn(), error: vi.fn() } } } })
    await flushPromises()

    await wrapper.get('button[title="檢視時程"]').trigger('click')
    await flushPromises()

    expect(get).toHaveBeenCalledWith(7, expect.objectContaining({ signal: expect.any(AbortSignal) }))
    expect(wrapper.text()).toContain('測試分期')
  })

  it('clears transaction rows immediately when the date query identity changes', async () => {
    const nextRequest = deferred<typeof transactionResponse>()
    const oldTransaction = {
      id: 9,
      type: 'Expense' as const,
      amount: 20,
      date: '2026-08-01',
      description: '舊期間資料',
      notes: null,
      categoryId: 1,
      paymentMethodId: null,
      createdAt: '2026-08-01T00:00:00Z',
      category: { id: 1, name: '餐飲', type: 'Expense' as const, icon: '', color: '', sortOrder: 1 },
      paymentMethod: null,
    }
    vi.spyOn(api.transactions, 'list')
      .mockResolvedValueOnce({ ...transactionResponse, items: [oldTransaction] })
      .mockReturnValueOnce(nextRequest.promise)
    vi.spyOn(api.categories, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 999 })
    vi.spyOn(api.paymentMethods, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 999 })
    vi.spyOn(api.creditCards, 'list').mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 999 })
    const wrapper = mount(ExpensesPage, { global: { plugins: [createTestRouter()], provide: { toast: { success: vi.fn(), error: vi.fn() } } } })
    await flushPromises()
    expect(wrapper.text()).toContain('舊期間資料')

    await wrapper.findAll('input[type="date"]')[0].setValue('2026-01-01')
    await flushPromises()
    expect(wrapper.text()).not.toContain('舊期間資料')

    nextRequest.resolve(transactionResponse)
    await flushPromises()
  })
})
