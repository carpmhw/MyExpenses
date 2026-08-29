import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { api } from '../../src/api'
import WithdrawalsPage from '../../src/pages/withdrawals/index.vue'
import ConfirmDialog from '../../src/components/ui/ConfirmDialog.vue'
import type { BankAccountListResponse, Withdrawal, WithdrawalListResponse } from '../../src/types'
import { createTestRouter, mountWithAppProviders } from '../support/render'

// 等待 Vue watcher 與非同步查詢完成目前排程。
async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await new Promise(resolve => setTimeout(resolve, 0))
}

const bankAccount = {
  id: 1,
  bankName: '測試銀行',
  accountNumber: '12345',
  accountType: '活期',
  balance: 1000,
  currencyCode: 'TWD' as const,
  createdAt: '2026-08-01T00:00:00Z',
  updatedAt: '2026-08-01T00:00:00Z',
  convertedBalance: 1000,
}

// 建立提款頁測試使用的銀行帳戶 response。
function createBankAccountResponse(): BankAccountListResponse {
  return {
    items: [bankAccount],
    total: 1,
    page: 1,
    pageSize: 999,
    totalBalanceInBaseCurrency: 1000,
    baseCurrency: 'TWD',
    exchangeRateUpdatedAt: null,
    exchangeRateIsStale: false,
    conversionAvailable: true,
  }
}

// 建立可覆寫匯率狀態的提款列表 response。
function createWithdrawalResponse(
  summaryOverrides: Partial<WithdrawalListResponse['summary']> = {},
): WithdrawalListResponse {
  const withdrawal: Withdrawal = {
    id: 1,
    amount: 100,
    date: '2026-08-01',
    description: '測試提款',
    bankAccountId: bankAccount.id,
    bankAccount,
  }
  return {
    items: [withdrawal],
    total: 1,
    page: 1,
    pageSize: 15,
    summary: {
      totalAmount: 100,
      count: 1,
      averageAmount: 100,
      maxAmount: 100,
      baseCurrency: 'TWD',
      exchangeRateUpdatedAt: null,
      exchangeRateIsStale: false,
      conversionAvailable: true,
      totalAmountInBaseCurrency: 100,
      ...summaryOverrides,
    },
  }
}

describe('withdrawal query reliability', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows an initial list error instead of synthetic zero values or an empty success state', async () => {
    vi.spyOn(api.bankAccounts, 'list').mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 999,
      totalBalanceInBaseCurrency: 0,
      baseCurrency: 'TWD',
      exchangeRateUpdatedAt: null,
      exchangeRateIsStale: false,
      conversionAvailable: true,
    })
    vi.spyOn(api.withdrawals, 'list').mockRejectedValue(new Error('匯率服務目前無法使用'))

    const wrapper = mountWithAppProviders(WithdrawalsPage)
    await flushPromises()

    expect(wrapper.text()).toContain('載入失敗')
    expect(wrapper.text()).toContain('重試')
    expect(wrapper.text()).not.toContain('尚無提款紀錄')
    expect(wrapper.text()).not.toContain('總提款金額')
    expect(wrapper.text()).not.toContain('共 0 筆')
    expect(wrapper.text()).not.toContain('1 / 1')
  })

  it('shows the stored update time when withdrawal conversion uses stale rates', async () => {
    vi.spyOn(api.bankAccounts, 'list').mockResolvedValue(createBankAccountResponse())
    vi.spyOn(api.withdrawals, 'list').mockResolvedValue(createWithdrawalResponse({
      exchangeRateIsStale: true,
      exchangeRateUpdatedAt: '2026-08-01T00:00:00Z',
    }))

    const wrapper = mountWithAppProviders(WithdrawalsPage)
    await flushPromises()

    expect(wrapper.text()).toContain('提款摘要使用過期匯率')
    expect(wrapper.text()).toContain('2026/08/01 08:00')
  })

  it('reports create success separately when the following refresh fails', async () => {
    const toast = { success: vi.fn(), error: vi.fn() }
    vi.spyOn(api.bankAccounts, 'list').mockResolvedValue(createBankAccountResponse())
    vi.spyOn(api.withdrawals, 'list')
      .mockResolvedValueOnce(createWithdrawalResponse())
      .mockRejectedValueOnce(new Error('refresh failed'))
    vi.spyOn(api.withdrawals, 'create').mockResolvedValue({
      id: 2,
      amount: 200,
      date: '2026-08-02',
      description: null,
      bankAccountId: bankAccount.id,
      bankAccount,
    })
    const wrapper = mount(WithdrawalsPage, {
      global: {
        plugins: [createTestRouter()],
        stubs: {
          Modal: {
            props: ['open'],
            template: '<div v-if="open" role="dialog"><slot /></div>',
          },
        },
        provide: {
          toast,
          timeZone: {
            timeZoneId: { value: 'Asia/Taipei' },
            isReady: { value: true },
            loadError: { value: false },
            getToday: () => '2026-08-02',
            formatDateTime: (value: string) => value,
          },
        },
      },
    })
    await flushPromises()

    await wrapper.findAll('button').find(button => button.text() === '+ 新增提款')!.trigger('click')
    const dialog = wrapper.get('[role="dialog"]')
    await dialog.get('input[type="number"]').setValue('200')
    await dialog.get('form').trigger('submit')
    await flushPromises()

    expect(toast.success).toHaveBeenCalledWith('提款已建立')
    expect(toast.error).toHaveBeenCalledWith('提款已成功，但資料重新整理失敗，請稍後重試')
    expect(toast.error).not.toHaveBeenCalledWith('儲存失敗')
    expect(wrapper.text()).toContain('測試提款')
    expect(wrapper.text()).toContain('資料可能已過期')
  })

  it('reports delete success separately when the following refresh fails', async () => {
    const toast = { success: vi.fn(), error: vi.fn() }
    vi.spyOn(api.bankAccounts, 'list').mockResolvedValue(createBankAccountResponse())
    vi.spyOn(api.withdrawals, 'list')
      .mockResolvedValueOnce(createWithdrawalResponse())
      .mockRejectedValueOnce(new Error('refresh failed'))
    vi.spyOn(api.withdrawals, 'delete').mockResolvedValue(undefined)
    const wrapper = mount(WithdrawalsPage, {
      global: {
        plugins: [createTestRouter()],
        provide: {
          toast,
          timeZone: {
            timeZoneId: { value: 'Asia/Taipei' },
            isReady: { value: true },
            loadError: { value: false },
            getToday: () => '2026-08-02',
            formatDateTime: (value: string) => value,
          },
        },
      },
    })
    await flushPromises()

    await wrapper.findAll('button').find(button => button.find('svg.lucide-trash-2').exists())!.trigger('click')
    wrapper.findComponent(ConfirmDialog).vm.$emit('confirm')
    await flushPromises()

    expect(toast.success).toHaveBeenCalledWith('提款已刪除')
    expect(toast.error).toHaveBeenCalledWith('提款已成功，但資料重新整理失敗，請稍後重試')
    expect(toast.error).not.toHaveBeenCalledWith('刪除失敗')
    expect(wrapper.text()).toContain('測試提款')
    expect(wrapper.text()).toContain('資料可能已過期')
  })
})
