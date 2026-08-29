import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../src/api'
import BankAccountsPage from '../../src/pages/bank-accounts/index.vue'
import { mountWithAppProviders } from '../support/render'

/** 清空 Vue 非同步 watcher 與 API promise queue。 */
async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

describe('銀行帳戶多幣別頁面', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    document.body.innerHTML = ''
  })

  /** 建立混合幣別且折合不可用的列表 response fixture。 */
  function createUnavailableResponse() {
    return {
      items: [
        {
          id: 1,
          bankName: '美元銀行',
          accountNumber: '12345',
          balance: 310,
          accountType: '活期',
          currencyCode: 'USD' as const,
          createdAt: '2026-08-01T00:00:00Z',
          updatedAt: '2026-08-01T00:00:00Z',
          convertedBalance: null,
        },
      ],
      total: 1,
      page: 1,
      pageSize: 15,
      baseCurrency: 'TWD' as const,
      totalBalanceInBaseCurrency: null,
      exchangeRateUpdatedAt: null,
      exchangeRateIsStale: false,
      conversionAvailable: false,
    }
  }

  /** 驗證頁面保留原幣並明確呈現折合不可用狀態。 */
  it('shows original currency and unavailable conversion state', async () => {
    vi.spyOn(api.bankAccounts, 'list').mockResolvedValue(createUnavailableResponse())

    const wrapper = mountWithAppProviders(BankAccountsPage)
    await flushPromises()

    expect(wrapper.text()).toContain('USD')
    expect(wrapper.text()).toContain('匯率不可用，保留原幣資料')
    expect(wrapper.text()).toContain('不可用')
  })

  /** 驗證編輯表單回填幣別且變更時顯示不自動換算提示。 */
  it('refills currency on edit and warns before changing it', async () => {
    vi.spyOn(api.bankAccounts, 'list').mockResolvedValue(createUnavailableResponse())

    const wrapper = mountWithAppProviders(BankAccountsPage)
    await flushPromises()
    await wrapper.find('tbody tr button').trigger('click')
    await flushPromises()

    const select = document.querySelector('#bank-account-currency') as HTMLSelectElement
    expect(select.value).toBe('USD')
    select.value = 'JPY'
    select.dispatchEvent(new Event('change'))
    await flushPromises()

    expect(document.body.textContent).toContain('變更幣別不會自動換算餘額')
  })
})
