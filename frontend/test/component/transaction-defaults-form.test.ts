import { flushPromises } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError } from '../../src/api'
import WithdrawalsPage from '../../src/pages/withdrawals/index.vue'
import InstallmentsPage from '../../src/pages/installments/index.vue'
import type { Installment, TransactionDefaults } from '../../src/types'
import { deferred } from '../support/deferred'
import { mountWithAppProviders } from '../support/render'

const accounts = [1, 2].map(id => ({ id, bankName: `銀行${id}`, accountNumber: '12345', currencyCode: 'TWD' as const, balance: 100, accountType: '活期', createdAt: '2026-08-01T00:00:00Z', updatedAt: '2026-08-01T00:00:00Z', convertedBalance: 100 }))
const cards = [1, 2].map(id => ({ id, bankName: `銀行${id}`, lastFourDigits: '1234', statementDay: 1, dueDay: 15, creditLimit: 1000, createdAt: '2026-08-01T00:00:00Z', updatedAt: '2026-08-01T00:00:00Z' }))
const withdrawal = { id: 7, bankAccountId: 1, bankAccount: accounts[0]!, amount: 100, date: '2026-08-02', description: '既有提款' }
const installment: Installment = { id: 7, transactionId: null, cardId: 1, card: cards[0]!, totalAmount: 100, periods: 1, perPeriod: 100, remainingPeriods: 1, status: 'Active', purchaseDate: '2026-08-02', createdAt: '2026-08-02T00:00:00Z', description: '既有交易', transaction: null, payments: [] }

// 種入完整的外部列表回應，表單與 query／mutation 邏輯使用真實元件。
function mockPageData(historyId = 1): void {
  vi.spyOn(api.bankAccounts, 'list').mockImplementation(async () => ({ items: [...accounts], total: 2, page: 1, pageSize: 100, baseCurrency: 'TWD', totalBalanceInBaseCurrency: 200, exchangeRateUpdatedAt: null, exchangeRateIsStale: false, conversionAvailable: true }))
  vi.spyOn(api.creditCards, 'list').mockImplementation(async () => ({ items: [...cards], total: 2, page: 1, pageSize: 100 }))
  vi.spyOn(api.withdrawals, 'list').mockResolvedValue({ items: [{ ...withdrawal, bankAccountId: historyId, bankAccount: { ...accounts[0]!, id: historyId } }], total: 1, page: 1, pageSize: 15, summary: { count: 1, totalAmount: 100, averageAmount: 100, maxAmount: 100, baseCurrency: 'TWD', exchangeRateUpdatedAt: null, exchangeRateIsStale: false, conversionAvailable: true, totalAmountInBaseCurrency: 100 } })
  vi.spyOn(api.installments, 'list').mockResolvedValue({ items: [{ ...installment, cardId: historyId, card: { ...cards[0]!, id: historyId } }], total: 1, page: 1, pageSize: 15, summary: { totalCount: 1, activeCount: 1, dueAmount: 100, duePaymentCount: 1 } })
  vi.spyOn(api.creditCardBills, 'list').mockResolvedValue([])
}

// 取得目前 modal，避免跨次開啟沿用已卸載的 DOM。
function dialog(): HTMLElement { return document.querySelector('[role="dialog"]')! }

// 取得實際選單，驗證使用者看見的選取值。
function select(): HTMLSelectElement { return dialog().querySelector('select')! }

// 透過原生 change 事件模擬使用者改選或清空。
function choose(value: string): void {
  select().value = value
  select().dispatchEvent(new Event('change', { bubbles: true }))
}

// 按取消關閉目前表單，讓下一次新增建立新的 session。
async function cancel(): Promise<void> {
  Array.from(dialog().querySelectorAll('button')).find(button => button.textContent === '取消')!.click()
  await flushPromises()
}

for (const kind of ['withdrawal', 'installment'] as const) {
  describe(`${kind} 新增固定預設`, () => {
    afterEach(() => { vi.restoreAllMocks(); document.body.innerHTML = '' })

    // 建立單一欄位的伺服器回應，另一類別不參與當次表單。
    function defaults(id: number | null): TransactionDefaults {
      return { defaultBankAccountId: kind === 'withdrawal' ? id : null, defaultCreditCardId: kind === 'installment' ? id : null }
    }

    // 掛載真實頁面；新增按鈕每次都重新讀取預設。
    async function mountPage(historyId = 1) {
      mockPageData(historyId)
      const wrapper = mountWithAppProviders(kind === 'withdrawal' ? WithdrawalsPage : InstallmentsPage)
      await flushPromises()
      return wrapper
    }

    // 透過頁面原有新增入口建立表單。
    async function open(wrapper: Awaited<ReturnType<typeof mountPage>>): Promise<void> {
      await wrapper.findAll('button').find(button => button.text().startsWith('+ 新增'))!.trigger('click')
      await flushPromises()
    }

    // 填入最少有效金融資料，不依賴預設替表單補上其他欄位。
    function fillDetails(periods = 1): void {
      const amount = dialog().querySelector<HTMLInputElement>('input[type="number"]')!
      amount.value = '100'
      amount.dispatchEvent(new Event('input', { bubbles: true }))
      if (kind === 'installment') {
        const description = dialog().querySelector<HTMLInputElement>('#credit-card-transaction-description')!
        description.value = '測試交易'
        description.dispatchEvent(new Event('input', { bubbles: true }))
        const count = dialog().querySelector<HTMLInputElement>('#credit-card-transaction-periods')!
        count.value = String(periods)
        count.dispatchEvent(new Event('input', { bubbles: true }))
      }
    }

    // 驗證僅偏好查詢失敗時，使用者仍能明確選擇並送出，且不隱含修改偏好。
    it.each(kind === 'installment' ? [1, 3] : [1])('submits a manual selection after a default query failure, periods=%s', async periods => {
      vi.spyOn(api.settings, 'getTransactionDefaults').mockRejectedValueOnce(new Error('offline')).mockResolvedValue(defaults(2))
      const bankDefault = vi.spyOn(api.bankAccounts, 'setDefault')
      const cardDefault = vi.spyOn(api.creditCards, 'setDefault')
      const bankCreate = vi.spyOn(api.withdrawals, 'create').mockResolvedValue(withdrawal)
      const cardCreate = vi.spyOn(api.installments, 'create').mockResolvedValue(installment)
      const wrapper = await mountPage()
      await open(wrapper)
      choose('1')
      fillDetails(periods)
      dialog().querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
      await flushPromises()
      if (kind === 'withdrawal') expect(bankCreate).toHaveBeenCalledWith(expect.objectContaining({ bankAccountId: 1, amount: 100 }))
      else expect(cardCreate).toHaveBeenCalledWith(expect.objectContaining({ cardId: 1, periods }), expect.any(String))
      expect(document.querySelector('[role="dialog"]')).toBeNull()
      expect(bankDefault).not.toHaveBeenCalled()
      expect(cardDefault).not.toHaveBeenCalled()
      await open(wrapper)
      expect(select().value).toBe('2')
      wrapper.unmount()
    })

    // 驗證提交後的延遲預設不改動畫面或已準備的明確金融命令。
    it('keeps the explicit command unchanged when a default arrives during submission', async () => {
      const pending = deferred<TransactionDefaults>()
      vi.spyOn(api.settings, 'getTransactionDefaults').mockReturnValue(pending.promise)
      const bankPending = deferred<typeof withdrawal>()
      const cardPending = deferred<Installment>()
      const bankCreate = vi.spyOn(api.withdrawals, 'create').mockReturnValue(bankPending.promise)
      const cardCreate = vi.spyOn(api.installments, 'create').mockReturnValue(cardPending.promise)
      const wrapper = await mountPage()
      await open(wrapper)
      choose('1')
      fillDetails()
      dialog().querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
      await flushPromises()
      pending.resolve(defaults(2))
      await flushPromises()
      expect(select().value).toBe('1')
      const create = kind === 'withdrawal' ? bankCreate : cardCreate
      expect(create.mock.calls[0]?.[0]).toEqual(expect.objectContaining(kind === 'withdrawal' ? { bankAccountId: 1 } : { cardId: 1 }))
      bankPending.resolve(withdrawal)
      cardPending.resolve(installment)
      await flushPromises()
      wrapper.unmount()
    })

    // 驗證重試更新候選分頁時，仍保留使用者已明確選擇但移到分頁外的項目。
    it('preserves a manual selection that moves outside the options page during retry', async () => {
      vi.spyOn(api.settings, 'getTransactionDefaults').mockRejectedValueOnce(new Error('offline')).mockResolvedValue(defaults(2))
      const wrapper = await mountPage()
      if (kind === 'withdrawal') vi.mocked(api.bankAccounts.list).mockResolvedValueOnce({ items: [...accounts, { ...accounts[0]!, id: 101 }], total: 101, page: 1, pageSize: 100, baseCurrency: 'TWD', totalBalanceInBaseCurrency: 300, exchangeRateUpdatedAt: null, exchangeRateIsStale: false, conversionAvailable: true })
      else vi.mocked(api.creditCards.list).mockResolvedValueOnce({ items: [...cards, { ...cards[0]!, id: 101 }], total: 101, page: 1, pageSize: 100 })
      await open(wrapper)
      choose('101')
      Array.from(dialog().querySelectorAll('button')).find(button => button.textContent === '重試預設設定')!.click()
      await flushPromises()
      expect(select().value).toBe('101')
      wrapper.unmount()
    })

    // 驗證預設非第一筆，並在下次新增時重新讀取其他裝置的更新。
    it('prefills the explicit default and reloads it for each new form', async () => {
      vi.spyOn(api.settings, 'getTransactionDefaults').mockResolvedValueOnce(defaults(2)).mockResolvedValue(defaults(1))
      const wrapper = await mountPage()
      await open(wrapper)
      expect(select().value).toBe('2')
      choose('1')
      await cancel()
      await open(wrapper)
      expect(select().value).toBe('1')
      wrapper.unmount()
    })

    // 驗證未設定時的空白選項與驗證，而非原生選單偷偷顯示第一筆。
    it('leaves an unset default blank and rejects a missing explicit selection', async () => {
      vi.spyOn(api.settings, 'getTransactionDefaults').mockResolvedValue(defaults(null))
      const create = kind === 'withdrawal' ? vi.spyOn(api.withdrawals, 'create') : vi.spyOn(api.installments, 'create')
      const wrapper = await mountPage()
      await open(wrapper)
      expect(select().value).toBe('')
      dialog().querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
      await flushPromises()
      expect(create).not.toHaveBeenCalled()
      expect(dialog().textContent).toContain(kind === 'withdrawal' ? '請選擇銀行帳戶' : '請選擇信用卡')
      wrapper.unmount()
    })

    // 驗證延遲回應不能覆蓋使用者改選或刻意清空。
    it.each(['1', ''])('does not overwrite an edited selection with a late default: %s', async value => {
      const pending = deferred<TransactionDefaults>()
      vi.spyOn(api.settings, 'getTransactionDefaults').mockReturnValue(pending.promise)
      const wrapper = await mountPage()
      await open(wrapper)
      choose(value)
      pending.resolve(defaults(2))
      await flushPromises()
      expect(select().value).toBe(value)
      wrapper.unmount()
    })

    // 驗證關閉後重開的表單不接受舊 session 的回應。
    it('ignores a previous form response after close and reopen', async () => {
      const old = deferred<TransactionDefaults>()
      vi.spyOn(api.settings, 'getTransactionDefaults').mockReturnValueOnce(old.promise).mockResolvedValue(defaults(1))
      const wrapper = await mountPage()
      await open(wrapper)
      await cancel()
      await open(wrapper)
      old.resolve(defaults(2))
      await flushPromises()
      expect(select().value).toBe('1')
      wrapper.unmount()
    })

    // 驗證編輯保持歷史 ID，即使之前的新增查詢仍在進行。
    it('keeps the original resource when editing after an unfinished new-form query', async () => {
      const pending = deferred<TransactionDefaults>()
      vi.spyOn(api.settings, 'getTransactionDefaults').mockReturnValue(pending.promise)
      const wrapper = await mountPage()
      await open(wrapper)
      await cancel()
      const edit = kind === 'withdrawal' ? wrapper.find('tbody tr button') : wrapper.find('button[title="編輯"]')
      await edit.trigger('click')
      pending.resolve(defaults(2))
      await flushPromises()
      expect(select().value).toBe('1')
      wrapper.unmount()
    })

    // 驗證分頁外預設曾被解析、又被後續新表單移除後，編輯仍明確顯示歷史項目。
    it('restores the historical option after later new forms replace the options page', async () => {
      vi.spyOn(api.settings, 'getTransactionDefaults').mockResolvedValueOnce(defaults(101)).mockResolvedValue(defaults(2))
      if (kind === 'withdrawal') vi.spyOn(api.bankAccounts, 'get').mockResolvedValue({ ...accounts[0]!, id: 101 })
      else vi.spyOn(api.creditCards, 'get').mockResolvedValue({ ...cards[0]!, id: 101 })
      const wrapper = await mountPage(101)
      await open(wrapper)
      expect(select().value).toBe('101')
      await cancel()
      await open(wrapper)
      expect(select().value).toBe('2')
      await cancel()
      const edit = kind === 'withdrawal' ? wrapper.find('tbody tr button') : wrapper.find('button[title="編輯"]')
      await edit.trigger('click')
      await flushPromises()
      expect(select().value).toBe('101')
      if (kind === 'installment') expect(dialog().querySelector('[data-testid="credit-card-transaction-schedule-preview"]')).not.toBeNull()
      wrapper.unmount()
    })

    // 驗證被分頁截斷的有效預設透過單筆 API 解析，不放寬列表上限。
    it('resolves a default outside the loaded options', async () => {
      vi.spyOn(api.settings, 'getTransactionDefaults').mockResolvedValue(defaults(101))
      if (kind === 'withdrawal') vi.spyOn(api.bankAccounts, 'get').mockResolvedValue({ ...accounts[0]!, id: 101, bankName: '遠端銀行' })
      else vi.spyOn(api.creditCards, 'get').mockResolvedValue({ ...cards[0]!, id: 101, bankName: '遠端銀行' })
      const wrapper = await mountPage()
      await open(wrapper)
      expect(select().value).toBe('101')
      expect(select().selectedOptions[0]!.textContent).toContain('遠端銀行')
      wrapper.unmount()
    })

    // 驗證查詢失敗、404 與網路錯誤不代選其他項目，重試不覆蓋手動輸入。
    it.each(['settings', 'missing', 'network'])('leaves an unavailable default blank and supports manual selection: %s', async failure => {
      const settings = vi.spyOn(api.settings, 'getTransactionDefaults')
      if (failure === 'settings') settings.mockRejectedValueOnce(new Error('offline')).mockResolvedValue(defaults(2))
      else {
        settings.mockResolvedValue(defaults(101))
        const error = failure === 'missing' ? new ApiError({ status: 404, title: 'not found', detail: null }) : new Error('offline')
        if (kind === 'withdrawal') vi.spyOn(api.bankAccounts, 'get').mockRejectedValue(error)
        else vi.spyOn(api.creditCards, 'get').mockRejectedValue(error)
      }
      const wrapper = await mountPage()
      await open(wrapper)
      expect(select().value).toBe('')
      expect(dialog().textContent).toContain(failure === 'missing' ? '預設項目已不存在' : '預設設定載入失敗')
      choose('1')
      const retry = Array.from(dialog().querySelectorAll('button')).find(button => button.textContent === '重試預設設定')!
      retry.click()
      await flushPromises()
      expect(select().value).toBe('1')
      wrapper.unmount()
    })
  })
}
