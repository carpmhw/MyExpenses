import { flushPromises } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../src/api'
import BankAccountsPage from '../../src/pages/bank-accounts/index.vue'
import CreditCardsPage from '../../src/pages/credit-cards/index.vue'
import ConfirmDialog from '../../src/components/ui/ConfirmDialog.vue'
import type { TransactionDefaults } from '../../src/types'
import { deferred } from '../support/deferred'
import { mountWithAppProviders } from '../support/render'

const accounts = [1, 2].map(id => ({ id, bankName: `銀行${id}`, accountNumber: '12345', currencyCode: 'TWD' as const, balance: 100, accountType: '活期', createdAt: '2026-08-01T00:00:00Z', updatedAt: '2026-08-01T00:00:00Z', convertedBalance: 100 }))
const cards = [1, 2].map(id => ({ id, bankName: `銀行${id}`, lastFourDigits: '1234', statementDay: 1, dueDay: 15, creditLimit: 1000, createdAt: '2026-08-01T00:00:00Z', updatedAt: '2026-08-01T00:00:00Z' }))

// 為兩個真實管理頁提供完整列表資料，僅替換外部 API 邊界。
function mockLists(): void {
  vi.spyOn(api.bankAccounts, 'list').mockResolvedValue({ items: accounts, total: 2, page: 1, pageSize: 15, baseCurrency: 'TWD', totalBalanceInBaseCurrency: 200, exchangeRateUpdatedAt: null, exchangeRateIsStale: false, conversionAvailable: true })
  vi.spyOn(api.creditCards, 'list').mockResolvedValue({ items: cards, total: 2, page: 1, pageSize: 15 })
}

for (const kind of ['bankAccounts', 'creditCards'] as const) {
  describe(`${kind} 固定預設管理`, () => {
    afterEach(() => { vi.restoreAllMocks(); document.body.innerHTML = '' })

    // 建立同一份預設 response，確保另一類別保持獨立。
    function defaults(id: number | null): TransactionDefaults {
      return kind === 'bankAccounts'
        ? { defaultBankAccountId: id, defaultCreditCardId: 9 }
        : { defaultBankAccountId: 9, defaultCreditCardId: id }
    }

    // 掛載真實管理頁並等待初次查詢。
    async function mountPage() {
      mockLists()
      const wrapper = mountWithAppProviders(kind === 'bankAccounts' ? BankAccountsPage : CreditCardsPage)
      await flushPromises()
      return wrapper
    }

    // 驗證空心／實心星星保留可讀名稱、滑鼠提示與單向設定語意。
    it('uses an accessible outline star action and a filled non-interactive default indicator', async () => {
      vi.spyOn(api.settings, 'getTransactionDefaults').mockResolvedValue(defaults(2))
      const wrapper = await mountPage()
      const action = wrapper.get('button[aria-label$="設為預設"]')
      expect(action.text()).toBe('')
      expect(action.attributes('type')).toBe('button')
      expect(action.attributes('title')).toBe('設為預設')
      expect(action.get('svg').attributes('fill')).toBe('none')
      expect(action.get('svg').attributes('aria-hidden')).toBe('true')
      const indicator = wrapper.get('[data-testid="default-indicator"]')
      expect(indicator.attributes('role')).toBe('img')
      expect(indicator.attributes('aria-label')).toContain('目前預設')
      expect(indicator.attributes('title')).toBe('目前預設')
      expect(indicator.get('svg').attributes('fill')).toBe('currentColor')
      expect(indicator.element.tagName).not.toBe('BUTTON')
      wrapper.unmount()
    })

    // 驗證伺服器指定而非列表第一筆決定標示，且寫入期間不能重複操作。
    it('marks the server default and replaces it only after a confirmed write', async () => {
      vi.spyOn(api.settings, 'getTransactionDefaults').mockResolvedValueOnce(defaults(2)).mockResolvedValue(defaults(1))
      const pending = deferred<TransactionDefaults>()
      vi.spyOn(api[kind], 'setDefault').mockReturnValue(pending.promise)
      const wrapper = await mountPage()
      expect(wrapper.findAll('tbody tr')[1]!.get('[data-testid="default-indicator"]').attributes('aria-label')).toContain('預設')
      expect(wrapper.findAll('[data-testid="default-indicator"]')).toHaveLength(1)
      const buttons = wrapper.findAll('button[aria-label$="設為預設"]')
      expect(buttons).toHaveLength(1)
      await buttons[0]!.trigger('click')
      expect(wrapper.findAll('tbody tr')[1]!.find('[data-testid="default-indicator"]').exists()).toBe(true)
      expect(buttons[0]!.attributes('disabled')).toBeDefined()
      pending.resolve(defaults(1))
      await flushPromises()
      expect(wrapper.findAll('tbody tr')[0]!.find('[data-testid="default-indicator"]').exists()).toBe(true)
      expect(wrapper.findAll('[data-testid="default-indicator"]')).toHaveLength(1)
      wrapper.unmount()
    })

    // 驗證寫入失敗不會樂觀覆蓋原預設。
    it('retains the previous marker and reports a failed write', async () => {
      vi.spyOn(api.settings, 'getTransactionDefaults').mockResolvedValue(defaults(2))
      vi.spyOn(api[kind], 'setDefault').mockRejectedValue(new Error('offline'))
      const wrapper = await mountPage()
      await wrapper.get('button[aria-label$="設為預設"]').trigger('click')
      await flushPromises()
      expect(wrapper.text()).toContain('設定預設失敗')
      expect(wrapper.findAll('tbody tr')[1]!.find('[data-testid="default-indicator"]').exists()).toBe(true)
      wrapper.unmount()
    })

    // 驗證未知狀態與未設定不同，重試成功後才顯示標示。
    it('shows an unknown state and retries a failed settings query', async () => {
      vi.spyOn(api.settings, 'getTransactionDefaults').mockRejectedValueOnce(new Error('offline')).mockResolvedValue(defaults(2))
      const wrapper = await mountPage()
      expect(wrapper.text()).toContain('無法確認預設設定')
      expect(wrapper.findAll('[data-testid="default-indicator"]')).toHaveLength(0)
      await wrapper.findAll('button').find(button => button.text() === '重試預設設定')!.trigger('click')
      await flushPromises()
      expect(wrapper.findAll('tbody tr')[1]!.find('[data-testid="default-indicator"]').exists()).toBe(true)
      wrapper.unmount()
    })

    // 驗證成功的設定與後續讀取失敗分開回報，保留成功回應的標示。
    it('keeps a confirmed write when the follow-up refresh fails', async () => {
      vi.spyOn(api.settings, 'getTransactionDefaults').mockResolvedValueOnce(defaults(2)).mockRejectedValue(new Error('refresh failed'))
      vi.spyOn(api[kind], 'setDefault').mockResolvedValue(defaults(1))
      const wrapper = await mountPage()
      await wrapper.get('button[aria-label$="設為預設"]').trigger('click')
      await flushPromises()
      expect(wrapper.text()).toContain('預設已設定，但重新整理失敗')
      expect(wrapper.text()).not.toContain('設定預設失敗')
      expect(wrapper.findAll('tbody tr')[0]!.find('[data-testid="default-indicator"]').exists()).toBe(true)
      wrapper.unmount()
    })

    // 驗證刪除成功重新讀取空預設，失敗則維持原標示。
    it.each([true, false])('refreshes defaults only after deletion succeeds: %s', async success => {
      vi.spyOn(api.settings, 'getTransactionDefaults').mockResolvedValueOnce(defaults(1)).mockResolvedValue(defaults(null))
      const remove = vi.spyOn(api[kind], 'delete')
      if (success) remove.mockResolvedValue(undefined)
      else remove.mockRejectedValue(new Error('restricted'))
      const wrapper = await mountPage()
      await wrapper.findAll('tbody tr')[0]!.findAll('button')[1]!.trigger('click')
      wrapper.findComponent(ConfirmDialog).vm.$emit('confirm')
      await flushPromises()
      expect(wrapper.findAll('[data-testid="default-indicator"]')).toHaveLength(success ? 0 : 1)
      wrapper.unmount()
    })
  })
}
