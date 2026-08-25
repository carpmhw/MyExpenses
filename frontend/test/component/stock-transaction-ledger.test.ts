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
})
