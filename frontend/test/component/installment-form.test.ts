import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import InstallmentsPage from '../../src/pages/installments/index.vue'
import { api } from '../../src/api'
import type { Installment } from '../../src/types'
import { mountWithAppProviders } from '../support/render'

// 等待信用卡交易頁的查詢與表單更新完成。
async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await new Promise(resolve => setTimeout(resolve, 0))
}

const card = {
  id: 3,
  bankName: '測試銀行',
  lastFourDigits: '1234',
  cardNetwork: 'Visa',
  statementDay: 15,
  dueDay: 23,
  creditLimit: 10000,
  notes: null,
  createdAt: '',
  updatedAt: '',
}

const onePeriodInstallment: Installment = {
  id: 7,
  transactionId: null,
  cardId: 3,
  totalAmount: 1200,
  periods: 1,
  perPeriod: 1200,
  remainingPeriods: 1,
  status: 'Active',
  purchaseDate: '2026-08-02',
  createdAt: '2026-08-02T00:00:00Z',
  description: '一次付清測試',
  transaction: null,
  card,
  payments: [{
    id: 71,
    installmentId: 7,
    period: 1,
    amount: 1200,
    paidDate: null,
    dueDate: '2026-08-23',
    isPaid: false,
  }],
}

// 建立信用卡交易頁所需的穩定 API 回應。
function mockPageData(items: Installment[] = []): void {
  vi.spyOn(api.installments, 'list').mockResolvedValue({
    items,
    total: items.length,
    page: 1,
    pageSize: 15,
    summary: {
      totalCount: items.length,
      activeCount: items.filter(item => item.status === 'Active').length,
      dueAmount: items.reduce((sum, item) => sum + item.payments.filter(payment => !payment.isPaid).reduce((paymentSum, payment) => paymentSum + payment.amount, 0), 0),
      duePaymentCount: items.reduce((count, item) => count + item.payments.filter(payment => !payment.isPaid).length, 0),
    },
  })
  vi.spyOn(api.creditCards, 'list').mockResolvedValue({ items: [card], total: 1, page: 1, pageSize: 999 })
  vi.spyOn(api.creditCardBills, 'list').mockResolvedValue([])
}

// 開啟信用卡交易新增視窗並回傳其 DOM 節點。
async function openCreateForm() {
  mockPageData()
  const wrapper = mountWithAppProviders(InstallmentsPage, { attachTo: document.body })
  await flushPromises()
  const createButton = wrapper.findAll('button').find(button => button.text().includes('新增'))
  expect(createButton).toBeDefined()
  await createButton!.trigger('click')
  await flushPromises()
  const dialog = document.body.querySelector('[role="dialog"]')
  expect(dialog).not.toBeNull()
  return { wrapper, dialog: dialog as HTMLElement }
}

// 設定原生輸入值並觸發元件使用的 input 事件。
function setInput(element: HTMLInputElement, value: string): void {
  element.value = value
  element.dispatchEvent(new Event('input', { bubbles: true }))
}

// 填入一期信用卡交易建立所需的欄位。
function fillOnePeriodForm(dialog: HTMLElement): void {
  setInput(dialog.querySelector<HTMLInputElement>('#credit-card-transaction-description')!, '一次付清測試')
  setInput(dialog.querySelector<HTMLInputElement>('#credit-card-transaction-total-amount')!, '1200')
  setInput(dialog.querySelector<HTMLInputElement>('#credit-card-transaction-periods')!, '1')
  const cardSelect = dialog.querySelector<HTMLSelectElement>('#credit-card-transaction-card')!
  cardSelect.value = '3'
  cardSelect.dispatchEvent(new Event('change', { bubbles: true }))
}

describe('信用卡交易表單', () => {
  afterEach(() => {
    document.body.innerHTML = ''
    vi.restoreAllMocks()
  })

  it('建立一期信用卡交易時送出 standalone installment command', async () => {
    const create = vi.spyOn(api.installments, 'create').mockResolvedValue(onePeriodInstallment)
    const transactionCreate = vi.spyOn(api.transactions, 'create')
    const { wrapper, dialog } = await openCreateForm()
    fillOnePeriodForm(dialog)

    dialog.querySelector<HTMLButtonElement>('button[type="submit"]')!.click()
    await flushPromises()

    expect(create).toHaveBeenCalledWith(expect.objectContaining({
      transactionId: null,
      cardId: 3,
      totalAmount: 1200,
      periods: 1,
    }), expect.any(String))
    expect(transactionCreate).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('接受六十期並拒絕超過上限、零期與非整數期數', async () => {
    const create = vi.spyOn(api.installments, 'create').mockResolvedValue(onePeriodInstallment)
    const { wrapper, dialog } = await openCreateForm()
    fillOnePeriodForm(dialog)

    const periods = dialog.querySelector<HTMLInputElement>('#credit-card-transaction-periods')!
    expect(periods.min).toBe('1')
    expect(periods.max).toBe('60')
    setInput(periods, '0')
    dialog.querySelector<HTMLButtonElement>('button[type="submit"]')!.click()
    await flushPromises()
    expect(dialog.textContent).toContain('期數必須為 1 至 60 期')
    expect(create).not.toHaveBeenCalled()

    setInput(periods, '1.5')
    dialog.querySelector<HTMLButtonElement>('button[type="submit"]')!.click()
    await flushPromises()
    expect(dialog.textContent).toContain('期數必須為 1 至 60 期')
    expect(create).not.toHaveBeenCalled()

    setInput(periods, '61')
    await flushPromises()
    expect(dialog.querySelector('[data-testid="credit-card-transaction-schedule-preview"]')).toBeNull()
    dialog.querySelector<HTMLButtonElement>('button[type="submit"]')!.click()
    await flushPromises()
    expect(dialog.textContent).toContain('期數必須為 1 至 60 期')
    expect(create).not.toHaveBeenCalled()

    setInput(periods, '60')
    await flushPromises()
    expect(dialog.querySelectorAll('[data-testid="credit-card-transaction-schedule-preview"]')).toHaveLength(1)
    dialog.querySelector<HTMLButtonElement>('button[type="submit"]')!.click()
    await flushPromises()
    expect(create).toHaveBeenCalledWith(expect.objectContaining({ periods: 60 }), expect.any(String))
    wrapper.unmount()
  })

  it('列表與編輯表單以一次付清呈現一期交易', async () => {
    mockPageData([onePeriodInstallment])
    const update = vi.spyOn(api.installments, 'update').mockResolvedValue(onePeriodInstallment)
    const wrapper = mountWithAppProviders(InstallmentsPage, { attachTo: document.body })
    await flushPromises()

    expect(wrapper.text()).toContain('1 期（一次付清）')
    await wrapper.find('button[title="編輯"]').trigger('click')
    await flushPromises()
    const dialog = document.body.querySelector('[role="dialog"]') as HTMLElement
    expect(dialog.textContent).toContain('編輯信用卡交易')
    expect(dialog.querySelector<HTMLInputElement>('#credit-card-transaction-periods')?.value).toBe('1')

    dialog.querySelector<HTMLButtonElement>('button[type="submit"]')!.click()
    await flushPromises()
    expect(update).toHaveBeenCalledWith(7, expect.objectContaining({ periods: 1 }))
    wrapper.unmount()
  })

  it('重試建立信用卡交易時沿用同一個冪等鍵', async () => {
    const create = vi.spyOn(api.installments, 'create')
      .mockRejectedValueOnce(new Error('network interrupted'))
      .mockResolvedValueOnce(onePeriodInstallment)
    const { wrapper, dialog } = await openCreateForm()
    fillOnePeriodForm(dialog)

    const submit = dialog.querySelector<HTMLButtonElement>('button[type="submit"]')!
    submit.click()
    await flushPromises()
    submit.click()
    await flushPromises()

    expect(create).toHaveBeenCalledTimes(2)
    expect(create.mock.calls[0]?.[1]).toBe(create.mock.calls[1]?.[1])
    wrapper.unmount()
  })

  it('同一表單改變內容後重試時使用新的冪等鍵', async () => {
    const create = vi.spyOn(api.installments, 'create')
      .mockRejectedValueOnce(new Error('network interrupted'))
      .mockResolvedValueOnce(onePeriodInstallment)
    const { wrapper, dialog } = await openCreateForm()
    fillOnePeriodForm(dialog)

    const submit = dialog.querySelector<HTMLButtonElement>('button[type="submit"]')!
    submit.click()
    await flushPromises()
    setInput(dialog.querySelector<HTMLInputElement>('#credit-card-transaction-description')!, '變更後的內容')
    submit.click()
    await flushPromises()

    expect(create).toHaveBeenCalledTimes(2)
    expect(create.mock.calls[0]?.[1]).not.toBe(create.mock.calls[1]?.[1])
    wrapper.unmount()
  })

  it('關閉後重開信用卡交易表單時使用新的冪等鍵', async () => {
    const create = vi.spyOn(api.installments, 'create')
      .mockRejectedValueOnce(new Error('network interrupted'))
      .mockResolvedValueOnce(onePeriodInstallment)
    const { wrapper, dialog } = await openCreateForm()
    fillOnePeriodForm(dialog)

    const submit = dialog.querySelector<HTMLButtonElement>('button[type="submit"]')!
    submit.click()
    await flushPromises()
    dialog.querySelector<HTMLButtonElement>('button[type="button"]')!.click()
    await flushPromises()

    const createButton = wrapper.findAll('button').find(button => button.text().includes('新增'))
    expect(createButton).toBeDefined()
    await createButton!.trigger('click')
    await flushPromises()
    const reopenedDialog = document.body.querySelector('[role="dialog"]') as HTMLElement
    fillOnePeriodForm(reopenedDialog)
    reopenedDialog.querySelector<HTMLButtonElement>('button[type="submit"]')!.click()
    await flushPromises()

    expect(create).toHaveBeenCalledTimes(2)
    expect(create.mock.calls[0]?.[1]).not.toBe(create.mock.calls[1]?.[1])
    wrapper.unmount()
  })

  it('信用卡選項載入失敗時顯示錯誤並提供重試', async () => {
    mockPageData()
    const cards = vi.mocked(api.creditCards.list)
      .mockRejectedValueOnce(new Error('cards unavailable'))
      .mockResolvedValueOnce({ items: [card], total: 1, page: 1, pageSize: 999 })
    const wrapper = mountWithAppProviders(InstallmentsPage, { attachTo: document.body })
    await flushPromises()

    expect(wrapper.text()).toContain('信用卡選項載入失敗')
    const retry = wrapper.findAll('button').find(button => button.text().includes('重試信用卡選項'))
    expect(retry).toBeDefined()
    await retry!.trigger('click')
    await flushPromises()

    expect(cards).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).not.toContain('信用卡選項載入失敗')
    wrapper.unmount()
  })
})
