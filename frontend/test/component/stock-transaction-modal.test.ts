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

  // 驗證歷史 Sell 不以目前 projection 的股數作為前端 hard validation。
  it('submits a historical sell even when current shares are zero', async () => {
    const closedStocks = [{ ...stocks[0], shares: 0, buyPrice: 0 }]
    const wrapper = mount(StockTransactionModal, {
      props: {
        open: true,
        stocks: closedStocks,
        stockId: 1,
        initialType: 'Buy',
        transaction: {
          id: 9,
          stockId: 1,
          stockName: '台積電',
          symbol: '2330',
          market: 'Twse',
          broker: '甲券商',
          type: 'Sell',
          tradeDate: '2026-02-01',
          sequence: 1,
          shares: 5,
          price: 550,
          fee: 0,
          tax: 0,
          cashAmount: null,
          openingMarketValue: null,
          notes: null,
          grossAmount: 2750,
          netCashFlow: 2750,
          allocatedCostBasis: 2500,
          realizedGainLoss: 250,
          netDividend: 0,
          remainingShares: 0,
          remainingCostBasis: 0,
          executionAveragePrice: 0,
        },
        loading: false,
      },
      global: {
        stubs: {
          Modal: { template: '<div v-if="open"><slot /></div>', props: ['open', 'title', 'closeDisabled'] },
        },
      },
    })

    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')

    expect(wrapper.emitted('save')).toEqual([[
      expect.objectContaining({ stockId: 1, type: 'Sell', shares: 5, price: 550 }),
    ]])
    expect(wrapper.text()).toContain('2330 台積電｜甲券商')
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

  // 驗證持股列的 Sell 快捷入口可將新增交易型別初始化為 Sell。
  it('uses the supplied initial transaction type for a new transaction', async () => {
    const wrapper = mount(StockTransactionModal, {
      props: { open: true, stocks, stockId: 1, transaction: null, initialType: 'Sell', loading: false },
      global: {
        stubs: {
          Modal: { template: '<div v-if="open"><slot /></div>', props: ['open', 'title', 'closeDisabled'] },
        },
      },
    })

    expect((wrapper.get('[data-testid="transaction-type"]').element as HTMLSelectElement).value).toBe('Sell')
  })

  // 驗證同代號不同券商與未設定券商都能在 selector 中被清楚辨識。
  it('renders broker-aware stock option labels and keeps StockId values', async () => {
    const options = [
      { ...stocks[0], id: 1, broker: '元大證券' },
      { ...stocks[0], id: 2, broker: '富邦證券' },
      { ...stocks[0], id: 3, broker: '   ' },
      { ...stocks[0], id: 4, broker: null },
    ]
    const wrapper = mount(StockTransactionModal, {
      props: { open: true, stocks: options, stockId: 2, transaction: null, loading: false },
      global: {
        stubs: {
          Modal: { template: '<div v-if="open"><slot /></div>', props: ['open', 'title', 'closeDisabled'] },
        },
      },
    })

    expect(wrapper.get('[data-testid="transaction-stock"]').text()).toContain('2330 台積電｜元大證券')
    expect(wrapper.get('[data-testid="transaction-stock"]').text()).toContain('2330 台積電｜富邦證券')
    expect(wrapper.get('[data-testid="transaction-stock"]').text()).toContain('2330 台積電｜未設定券商')
    expect(wrapper.get('[data-testid="transaction-stock-summary"]').text()).toContain('2330 台積電｜富邦證券')
    expect(wrapper.get('option[value="2"]').text()).toContain('富邦證券')
    await wrapper.get('[data-testid="transaction-shares"]').setValue('1')
    await wrapper.get('[data-testid="transaction-price"]').setValue('550')
    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')
    expect(wrapper.emitted('save')?.[0]?.[0]).toEqual(expect.objectContaining({ stockId: 2 }))
  })
})
