import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import StockTransactionLedger from '../../src/components/stocks/StockTransactionLedger.vue'

const item = {
  id: 1,
  stockId: 1,
  stockName: '台積電',
  symbol: '2330',
  market: 'Twse' as const,
  broker: '甲券商',
  type: 'Buy' as const,
  tradeDate: '2026-01-01',
  sequence: 1,
  shares: 10,
  price: 500,
  fee: 0,
  tax: 0,
  cashAmount: null,
  openingMarketValue: null,
  notes: '第一筆買入',
  grossAmount: 5000,
  netCashFlow: -5000,
  allocatedCostBasis: null,
  realizedGainLoss: 0,
  netDividend: 0,
  remainingShares: 10,
  remainingCostBasis: 5000,
  executionAveragePrice: 500,
}

const stockDividendItem = {
  ...item,
  id: 2,
  type: 'StockDividend' as const,
  shares: 100,
  price: null,
  cashAmount: null,
  notes: null,
  grossAmount: 0,
  netCashFlow: 0,
  allocatedCostBasis: null,
  realizedGainLoss: 0,
  remainingShares: 110,
  remainingCostBasis: 5000,
  executionAveragePrice: 45.45,
}

const cashDividendItem = {
  ...item,
  id: 3,
  type: 'Dividend' as const,
  shares: null,
  price: null,
  cashAmount: 500,
  notes: null,
}

describe('StockTransactionLedger', () => {
  // 驗證交易列提供 edit/delete mutation 入口，並保留穩定 transaction id。
  it('emits edit and delete actions for a transaction', async () => {
    const wrapper = mount(StockTransactionLedger, {
      props: { items: [item], loading: false, total: 1, hasStocks: true, page: 1, pageSize: 20 },
    })

    await wrapper.get('[data-testid="ledger-edit-1"]').trigger('click')
    await wrapper.get('[data-testid="ledger-delete-1"]').trigger('click')

    expect(wrapper.emitted('edit')).toEqual([[item]])
    expect(wrapper.emitted('delete')).toEqual([[1]])
  })

  // 驗證分頁控制只在對應邊界提供可用的上一頁／下一頁操作。
  it('emits ledger page changes', async () => {
    const wrapper = mount(StockTransactionLedger, {
      props: { items: [item], loading: false, total: 41, hasStocks: true, page: 2, pageSize: 20 },
    })

    await wrapper.get('[data-testid="ledger-page-prev"]').trigger('click')
    await wrapper.get('[data-testid="ledger-page-next"]').trigger('click')

    expect(wrapper.emitted('previous')).toEqual([[]])
    expect(wrapper.emitted('next')).toEqual([[]])
  })

  // 驗證無資料時顯示明確 empty state，並保留窄螢幕 local overflow 容器。
  it('renders an empty state for an empty ledger', () => {
    const wrapper = mount(StockTransactionLedger, {
      props: { items: [], loading: false, total: 0, hasStocks: true, page: 1, pageSize: 20 },
    })

    expect(wrapper.text()).toContain('尚無交易紀錄')
    expect(wrapper.find('.overflow-x-auto').exists()).toBe(false)
  })

  // 驗證現金股利與股票股利使用不同標籤，股票股利不顯示無意義的零元金額。
  it('renders distinct dividend labels and omits stock dividend money values', () => {
    const wrapper = mount(StockTransactionLedger, {
      props: { items: [cashDividendItem, stockDividendItem], loading: false, total: 2, hasStocks: true, page: 1, pageSize: 20 },
    })

    const rows = wrapper.findAll('tbody tr')
    expect(rows[0]?.text()).toContain('現金股利')
    expect(rows[1]?.text()).toContain('股票股利')
    expect(rows[1]?.text()).toContain('100')
    expect(rows[1]?.text()).not.toContain('NT$ 0')
  })
})
