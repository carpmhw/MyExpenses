import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import StockTransactionModal from '../../src/components/stocks/StockTransactionModal.vue'
import type { StockListItem } from '../../src/types'

const stocks: StockListItem[] = [{
  id: 1,
  name: '台積電',
  symbol: '2330',
  market: 'Twse',
  instrumentType: 'Stock',
  shares: 10,
  buyPrice: 500,
  currentPrice: 600,
  broker: '甲券商',
  lastPriceUpdate: null,
  grossMarketValue: 6000,
  buyCommission: 0,
  sellCommission: 0,
  securitiesTransactionTax: 0,
  estimatedNetSellValue: 6000,
  estimatedGainLoss: 1000,
  hasLedger: true,
}]

describe('StockTransactionModal', () => {
  // 驗證 Buy 表單將原生輸入值轉換成 Ledger API request。
  it('emits a buy transaction request', async () => {
    const wrapper = mount(StockTransactionModal, {
      props: { open: true, stocks, stockId: 1, transaction: null, loading: false },
      global: {
        stubs: {
          Modal: { template: '<div v-if="open"><slot /></div>', props: ['open', 'title', 'closeDisabled'] },
        },
      },
    })

    await wrapper.get('[data-testid="transaction-type"]').setValue('Buy')
    await wrapper.get('[data-testid="transaction-trade-date"]').setValue('2026-08-01')
    await wrapper.get('[data-testid="transaction-shares"]').setValue('10')
    await wrapper.get('[data-testid="transaction-price"]').setValue('500')
    await wrapper.get('[data-testid="transaction-fee"]').setValue('2')
    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')

    expect(wrapper.emitted('save')).toEqual([[
      expect.objectContaining({
        stockId: 1,
        type: 'Buy',
        tradeDate: '2026-08-01',
        shares: 10,
        price: 500,
        fee: 2,
        tax: 0,
        cashAmount: null,
      }),
    ]])
  })

  // 驗證 Dividend 只顯示現金欄位，避免送出與交易型別衝突的 shares/price。
  it('emits a dividend request without share fields', async () => {
    const wrapper = mount(StockTransactionModal, {
      props: { open: true, stocks, stockId: 1, transaction: null, loading: false },
      global: {
        stubs: {
          Modal: { template: '<div v-if="open"><slot /></div>', props: ['open', 'title', 'closeDisabled'] },
        },
      },
    })

    await wrapper.get('[data-testid="transaction-type"]').setValue('Dividend')
    expect(wrapper.find('[data-testid="transaction-shares"]').exists()).toBe(false)
    await wrapper.get('[data-testid="transaction-cash-amount"]').setValue('100')
    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')

    expect(wrapper.emitted('save')?.[0]?.[0]).toEqual(expect.objectContaining({
      type: 'Dividend',
      shares: null,
      price: null,
      cashAmount: 100,
    }))
  })

  // 驗證 client 先擋住超過目前 projection 的 Sell，減少不必要的 server mutation。
  it('prevents an oversell before sending the request', async () => {
    const wrapper = mount(StockTransactionModal, {
      props: { open: true, stocks, stockId: 1, transaction: null, loading: false },
      global: {
        stubs: {
          Modal: { template: '<div v-if="open"><slot /></div>', props: ['open', 'title', 'closeDisabled'] },
        },
      },
    })

    await wrapper.get('[data-testid="transaction-type"]').setValue('Sell')
    await wrapper.get('[data-testid="transaction-shares"]').setValue('11')
    await wrapper.get('[data-testid="transaction-price"]').setValue('550')
    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')

    expect(wrapper.emitted('save')).toBeUndefined()
    expect(wrapper.text()).toContain('可用股數不足')
  })

  // 驗證 OpeningBalance 不會出現在一般新增選單，編輯時只顯示不可直接修改警告。
  it('does not offer OpeningBalance for new transactions', async () => {
    const wrapper = mount(StockTransactionModal, {
      props: { open: true, stocks, stockId: 1, transaction: null, loading: false },
      global: {
        stubs: {
          Modal: { template: '<div v-if="open"><slot /></div>', props: ['open', 'title', 'closeDisabled'] },
        },
      },
    })

    expect(wrapper.get('[data-testid="transaction-type"]').text()).not.toContain('期初部位')
  })
})
