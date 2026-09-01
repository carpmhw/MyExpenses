import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { ApiError, api } from '../../src/api'
import ExpensesPage from '../../src/pages/expenses/index.vue'
import TransactionForm from '../../src/components/transactions/TransactionForm.vue'
import { mountWithAppProviders } from '../support/render'
import { deferred } from '../support/deferred'
import { createInitialTransactionForm } from '../../src/utils/transactionForm'

async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
  await nextTick()
}

const transactionResponse = {
  items: [],
  total: 0,
  page: 1,
  pageSize: 15,
  summary: { totalAmount: 0, totalIncome: 0, totalExpense: 0, count: 0, dailyAverage: 0, maxAmount: 0 },
}

function mockReferenceData(): void {
  vi.spyOn(api.transactions, 'list').mockResolvedValue(transactionResponse)
  vi.spyOn(api.categories, 'list').mockResolvedValue({
    items: [
      { id: 1, name: '餐飲', type: 'Expense', icon: 'utensils', color: '#2563eb', sortOrder: 1 },
      { id: 2, name: '薪資', type: 'Income', icon: 'wallet', color: '#16a34a', sortOrder: 2 },
    ],
    total: 2,
    page: 1,
    pageSize: 999,
  })
  vi.spyOn(api.paymentMethods, 'list').mockResolvedValue({
    items: [
      { id: 10, name: '信用卡', systemCode: 'credit-card', icon: 'credit-card', sortOrder: 1, color: '#2563eb' },
      { id: 11, name: '現金', systemCode: 'cash', icon: 'banknote', sortOrder: 2, color: '#16a34a' },
    ],
    total: 2,
    page: 1,
    pageSize: 999,
  })
  vi.spyOn(api.creditCards, 'list').mockResolvedValue({
    items: [{
      id: 7,
      bankName: '測試銀行',
      lastFourDigits: '1234',
      cardNetwork: 'Visa',
      statementDay: 15,
      dueDay: 23,
      creditLimit: 10000,
      notes: null,
      createdAt: '',
      updatedAt: '',
    }],
    total: 1,
    page: 1,
    pageSize: 999,
  })
}

async function openTransactionForm(configure?: () => void) {
  mockReferenceData()
  configure?.()
  const wrapper = mountWithAppProviders(ExpensesPage, { attachTo: document.body })
  await flushPromises()
  await wrapper.get('button').trigger('click')
  await flushPromises()
  const dialog = document.body.querySelector('[role="dialog"]')
  expect(dialog).not.toBeNull()
  return { wrapper, dialog: dialog as HTMLElement }
}

// 填入一般支出所需的最小有效欄位。
function fillOrdinaryExpense(dialog: HTMLElement): void {
  const amount = dialog.querySelector<HTMLInputElement>('#transaction-amount')!
  amount.value = '1280'
  amount.dispatchEvent(new Event('input'))
  const description = dialog.querySelector<HTMLInputElement>('#transaction-description')!
  description.value = '晚餐'
  description.dispatchEvent(new Event('input'))
}

describe('transaction form interaction contract', () => {
  afterEach(() => {
    document.body.innerHTML = ''
    vi.restoreAllMocks()
  })

  it('places the date control first and focuses it when a new form opens', async () => {
    const { wrapper, dialog } = await openTransactionForm()

    const formControls = [...dialog.querySelectorAll('form input, form select, form textarea, form button')]
    expect(formControls[0]?.getAttribute('type')).toBe('date')
    expect(document.activeElement).toBe(formControls[0])
    wrapper.unmount()
  })

  it('does not show required-field errors before the owner interacts or submits', async () => {
    const { wrapper, dialog } = await openTransactionForm()

    expect(dialog.textContent).not.toContain('金額必須大於零')
    expect(dialog.textContent).not.toContain('請選擇類別')
    expect(dialog.textContent).not.toContain('請填寫項目名稱')
    wrapper.unmount()
  })

  it('clears an expense category when expense becomes income', async () => {
    const { wrapper, dialog } = await openTransactionForm()

    const type = dialog.querySelector<HTMLSelectElement>('#transaction-type')
    expect(type).not.toBeNull()

    type!.value = 'Income'
    type!.dispatchEvent(new Event('change'))
    await flushPromises()

    expect(dialog.querySelector<HTMLSelectElement>('#transaction-category')?.value).toBe('')
    expect(dialog.querySelector<HTMLSelectElement>('#transaction-payment-method')?.value).toBe('')
    wrapper.unmount()
  })

  it('preserves neutral values when transaction type changes', async () => {
    const { wrapper, dialog } = await openTransactionForm()

    const date = dialog.querySelector<HTMLInputElement>('#transaction-date')!
    const amount = dialog.querySelector<HTMLInputElement>('#transaction-amount')!
    const description = dialog.querySelector<HTMLInputElement>('#transaction-description')!
    const notes = dialog.querySelector<HTMLInputElement>('#transaction-notes')!
    date.value = '2026-08-01'
    date.dispatchEvent(new Event('input'))
    amount.value = '1280'
    amount.dispatchEvent(new Event('input'))
    description.value = '晚餐'
    description.dispatchEvent(new Event('input'))
    notes.value = '測試備註'
    notes.dispatchEvent(new Event('input'))

    const type = dialog.querySelector<HTMLSelectElement>('#transaction-type')!
    type.value = 'Income'
    type.dispatchEvent(new Event('change'))
    await flushPromises()

    expect(date.value).toBe('2026-08-01')
    expect(amount.value).toBe('1280')
    expect(description.value).toBe('晚餐')
    expect(notes.value).toBe('測試備註')
    wrapper.unmount()
  })

  it('sends only one command when the owner submits repeatedly while pending', async () => {
    const request = deferred<unknown>()
    const create = vi.spyOn(api.transactions, 'create').mockReturnValue(request.promise)
    const { wrapper, dialog } = await openTransactionForm()
    fillOrdinaryExpense(dialog)

    const submit = dialog.querySelector<HTMLButtonElement>('button[type="submit"]')!
    submit.click()
    submit.click()
    await flushPromises()

    expect(create).toHaveBeenCalledTimes(1)
    expect(submit.disabled).toBe(true)
    request.resolve({})
    await flushPromises()
    wrapper.unmount()
  })

  it('keeps the form open and shows a confirmed API failure', async () => {
    vi.spyOn(api.transactions, 'create').mockRejectedValue(new ApiError({ status: 422, userMessage: '項目名稱格式不正確' }))
    const { wrapper, dialog } = await openTransactionForm()
    fillOrdinaryExpense(dialog)

    dialog.querySelector<HTMLButtonElement>('button[type="submit"]')!.click()
    await flushPromises()
    await flushPromises()

    expect(document.body.querySelector('[role="dialog"]')).not.toBeNull()
    expect(document.body.textContent).toContain('項目名稱格式不正確')
    expect(document.activeElement?.textContent).toContain('項目名稱格式不正確')
    wrapper.unmount()
  })

  it('preserves the form and explains an uncertain ordinary transaction outcome', async () => {
    vi.spyOn(api.transactions, 'create').mockRejectedValue(new Error('network interrupted'))
    const { wrapper, dialog } = await openTransactionForm()
    fillOrdinaryExpense(dialog)

    dialog.querySelector<HTMLButtonElement>('button[type="submit"]')!.click()
    await flushPromises()
    await flushPromises()

    expect(document.body.querySelector('[role="dialog"]')).not.toBeNull()
    expect(document.body.textContent).toContain('無法確認交易是否已建立')
    expect(document.body.textContent).toContain('重新整理交易列表')
    expect(document.body.textContent).not.toContain('可使用相同資料安全重試')
    expect(document.body.querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(true)
    expect(document.body.querySelector<HTMLInputElement>('#transaction-amount')?.disabled).toBe(true)
    wrapper.unmount()
  })

  it('treats authorization rejection as a confirmed failure', async () => {
    vi.spyOn(api.transactions, 'create').mockRejectedValue(new ApiError({ status: 403, userMessage: '沒有建立交易的權限' }))
    const { wrapper, dialog } = await openTransactionForm()
    fillOrdinaryExpense(dialog)

    dialog.querySelector<HTMLButtonElement>('button[type="submit"]')!.click()
    await flushPromises()
    await flushPromises()

    expect(document.body.textContent).toContain('沒有建立交易的權限')
    expect(document.body.textContent).not.toContain('無法確認交易是否已建立')
    wrapper.unmount()
  })

  it('marks the list stale instead of reporting command failure after refresh fails', async () => {
    const list = vi.spyOn(api.transactions, 'list')
    vi.spyOn(api.transactions, 'create').mockResolvedValue({} as never)
    const { wrapper, dialog } = await openTransactionForm()
    list.mockRejectedValueOnce(new Error('refresh interrupted'))
    fillOrdinaryExpense(dialog)

    dialog.querySelector<HTMLButtonElement>('button[type="submit"]')!.click()
    await flushPromises()
    await flushPromises()

    expect(list).toHaveBeenCalledTimes(2)
    expect(document.body.textContent).toContain('資料可能已過期')
    expect(document.body.textContent).not.toContain('儲存失敗')
    wrapper.unmount()
  })

  it('does not offer credit-card payment to an income transaction', async () => {
    const { wrapper, dialog } = await openTransactionForm()
    const type = dialog.querySelector<HTMLSelectElement>('#transaction-type')!
    type.value = 'Income'
    type.dispatchEvent(new Event('change'))
    await flushPromises()

    const paymentOptions = [...dialog.querySelectorAll<HTMLSelectElement>('#transaction-payment-method option')]
      .map(option => option.textContent)
    expect(paymentOptions).not.toContain('信用卡')
    wrapper.unmount()
  })

  it('excludes credit-card payment and installment controls from new transactions', async () => {
    const { wrapper, dialog } = await openTransactionForm()

    const paymentOptions = [...dialog.querySelectorAll<HTMLSelectElement>('#transaction-payment-method option')]
      .map(option => option.textContent)
    expect(paymentOptions).not.toContain('信用卡')
    expect(dialog.querySelector('#transaction-payment-mode')).toBeNull()
    expect(dialog.querySelector('#transaction-installment-card')).toBeNull()
    expect(dialog.querySelector('#transaction-installment-periods')).toBeNull()
    wrapper.unmount()
  })

  it('renders a historical credit-card payment as read-only and preserves its identifier', async () => {
    const update = vi.fn()
    const form = mount(TransactionForm, {
      props: {
        initialValue: {
          ...createInitialTransactionForm('2026-08-03', [{ id: 1, name: '餐飲', type: 'Expense', icon: '', color: '', sortOrder: 1 }]),
          amount: 300,
          description: '舊信用卡交易',
          paymentMethodId: 10,
        },
        categories: [{ id: 1, name: '餐飲', type: 'Expense', icon: '', color: '', sortOrder: 1 }],
        paymentMethods: [
          { id: 10, name: '自訂信用卡', systemCode: 'credit-card', icon: '', sortOrder: 1, color: '#2563eb' },
          { id: 11, name: '現金', systemCode: 'cash', icon: '', sortOrder: 2, color: '' },
        ],
        editing: {
          id: 42,
          type: 'Expense',
          amount: 300,
          date: '2026-08-03',
          description: '舊信用卡交易',
          notes: null,
          categoryId: 1,
          paymentMethodId: 10,
          createdAt: '',
          category: { id: 1, name: '餐飲', type: 'Expense', icon: '', color: '', sortOrder: 1 },
          paymentMethod: { id: 10, name: '自訂信用卡', systemCode: 'credit-card', icon: '', sortOrder: 1, color: '#2563eb' },
        },
        onSubmit: update,
      },
    })
    await flushPromises()

    const paymentMethod = form.element.querySelector<HTMLInputElement>('#transaction-payment-method')
    expect(paymentMethod?.readOnly).toBe(true)
    expect(paymentMethod?.value).toBe('信用卡')
    expect(form.element.querySelector<HTMLSelectElement>('#transaction-type')?.disabled).toBe(true)
    await form.trigger('submit')
    await flushPromises()

    expect(update).toHaveBeenCalledWith(expect.objectContaining({
      kind: 'update',
      data: expect.objectContaining({ paymentMethodId: 10 }),
    }))
    form.unmount()
  })

  it('does not load credit-card reference data for ordinary transaction entry', async () => {
    const { wrapper } = await openTransactionForm()

    expect(api.creditCards.list).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('emits an ordinary update command when editing an existing transaction', async () => {
    const update = vi.fn()
    const form = mount(TransactionForm, {
      props: {
        initialValue: createInitialTransactionForm('2026-08-03', [{ id: 1, name: '餐飲', type: 'Expense', icon: '', color: '', sortOrder: 1 }]),
        categories: [{ id: 1, name: '餐飲', type: 'Expense', icon: '', color: '', sortOrder: 1 }],
        paymentMethods: [{ id: 11, name: '現金', systemCode: 'cash', icon: '', sortOrder: 1, color: '' }],
        editing: {
          id: 42,
          type: 'Expense',
          amount: 300,
          date: '2026-08-03',
          description: '舊交易',
          notes: null,
          categoryId: 1,
          paymentMethodId: 11,
          createdAt: '',
          category: { id: 1, name: '餐飲', type: 'Expense', icon: '', color: '', sortOrder: 1 },
          paymentMethod: null,
        },
        onSubmit: update,
      },
    })
    await flushPromises()
    const root = form.element
    root.querySelector<HTMLInputElement>('#transaction-amount')!.value = '500'
    root.querySelector<HTMLInputElement>('#transaction-amount')!.dispatchEvent(new Event('input'))
    root.querySelector<HTMLInputElement>('#transaction-description')!.value = '修改後'
    root.querySelector<HTMLInputElement>('#transaction-description')!.dispatchEvent(new Event('input'))
    await form.trigger('submit')
    await flushPromises()

    expect(update).toHaveBeenCalledWith(expect.objectContaining({ kind: 'update', id: 42 }))
    expect(update.mock.calls[0][0].kind).toBe('update')
    form.unmount()
  })

  it('disables submit and exposes retry when category reference data fails', async () => {
    let categories: ReturnType<typeof vi.spyOn> | undefined
    await openTransactionForm(() => {
      categories = vi.spyOn(api.categories, 'list').mockRejectedValueOnce(new Error('categories unavailable'))
    })

    const dialog = document.body.querySelector('[role="dialog"]') as HTMLElement
    expect(dialog.textContent).toContain('分類或支付方式資料載入失敗')
    expect(dialog.querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(true)
    categories!.mockResolvedValueOnce({
      items: [{ id: 1, name: '餐飲', type: 'Expense', icon: 'utensils', color: '#2563eb', sortOrder: 1 }],
      total: 1,
      page: 1,
      pageSize: 999,
    })
    Array.from(dialog.querySelectorAll('button')).find(button => button.textContent?.trim() === '重試')?.click()
    await flushPromises()
    expect(dialog.textContent).not.toContain('分類或支付方式資料載入失敗')
  })

  it('disables submit and exposes retry when payment-method reference data fails', async () => {
    await openTransactionForm(() => {
      vi.spyOn(api.paymentMethods, 'list').mockRejectedValueOnce(new Error('payment methods unavailable'))
    })

    const dialog = document.body.querySelector('[role="dialog"]') as HTMLElement
    expect(dialog.textContent).toContain('分類或支付方式資料載入失敗')
    expect(dialog.querySelector<HTMLButtonElement>('button[type="submit"]')?.disabled).toBe(true)
  })

})
