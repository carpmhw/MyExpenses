import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import StockHoldingsTable from '../../src/components/stocks/StockHoldingsTable.vue'

function createItem(hasLedger: boolean) {
  return {
    id: hasLedger ? 1 : 2,
    name: '台積電',
    symbol: '2330',
    market: 'Twse' as const,
    instrumentType: 'Stock' as const,
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
    hasLedger,
  }
}

describe('StockHoldingsTable', () => {
  // 驗證 Ledger-managed 持股的刪除按鈕停用，且 legacy 持股仍可發出刪除事件。
  it('disables Ledger deletion and emits legacy deletion', async () => {
    const wrapper = mount(StockHoldingsTable, {
      props: { items: [createItem(true), createItem(false)], loading: false, page: 1, pageSize: 15 },
    })

    const ledgerDelete = wrapper.get('[data-testid="stock-delete-1"]')
    const legacyDelete = wrapper.get('[data-testid="stock-delete-2"]')
    expect(ledgerDelete.attributes('disabled')).toBeDefined()

    await legacyDelete.trigger('click')
    expect(wrapper.emitted('delete')).toEqual([[2]])
  })

  // 驗證每筆持股列可直接發出 Buy 與 Sell 快捷事件並保留完整持股資料。
  it('emits Buy and Sell quick actions for a holding', async () => {
    const item = createItem(true)
    const wrapper = mount(StockHoldingsTable, {
      props: { items: [item], loading: false, page: 1, pageSize: 15 },
    })

    await wrapper.get('[data-testid="stock-buy-1"]').trigger('click')
    await wrapper.get('[data-testid="stock-sell-1"]').trigger('click')

    expect(wrapper.emitted('buy')).toEqual([[item]])
    expect(wrapper.emitted('sell')).toEqual([[item]])
  })
})
