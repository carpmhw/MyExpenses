import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import StockLedgerInitialization from '../../src/components/stocks/StockLedgerInitialization.vue'
import type { StockLedgerInitializationResponse } from '../../src/types'

describe('StockLedgerInitialization', () => {
  // 驗證初始化說明清楚揭露 baseline boundary 且送出系統日期。
  it('explains the tracking boundary and emits the selected baseline date', async () => {
    const wrapper = mount(StockLedgerInitialization, {
      props: { hasActiveHoldings: true, loading: false, response: null, holdings: [{ shares: 10, currentPrice: 600 }] },
    })

    expect(wrapper.text()).toContain('不推測歷史買入日期')
    expect(wrapper.text()).toContain('baseline')
    expect(wrapper.text()).toContain('期初市值約 NT$ 6,000')
    const date = wrapper.get('[data-testid="ledger-baseline-date"]').element as HTMLInputElement
    expect(date.value).toMatch(/^\d{4}-\d{2}-\d{2}$/)

    await wrapper.get('[data-testid="ledger-baseline-date"]').setValue('2026-08-01')
    await wrapper.get('[data-testid="initialize-ledger"]').trigger('click')
    expect(wrapper.emitted('initialize')).toEqual([['2026-08-01']])
  })

  // 驗證初始化簡易說明預設收合，且展開內容涵蓋作用、基準日期與資料前置條件。
  it('renders collapsed initialization help content', () => {
    const wrapper = mount(StockLedgerInitialization, {
      props: { hasActiveHoldings: true, loading: false, response: null },
    })
    const details = wrapper.get('[data-testid="ledger-initialization-help"]')

    expect((details.element as HTMLDetailsElement).open).toBe(false)
    expect(details.get('summary').text()).toContain('這是什麼？')
    expect(details.text()).toContain('尚未有交易紀錄的既有持股')
    expect(details.text()).toContain('baseline date')
    expect(details.text()).toContain('缺少買入均價或目前價格')
  })

  // 驗證初始化結果保留 typed blocking stocks，不把阻擋原因折疊成一般錯誤。
  it('renders initialized counts and typed blocking stocks', () => {
    const response: StockLedgerInitializationResponse = {
      initializedCount: 1,
      skippedCount: 2,
      blockingCount: 1,
      totalCount: 4,
      blockingStocks: [{
        stockId: 3,
        symbol: '2330',
        reason: 'MissingBuyPrice',
        code: 'MissingBuyPrice',
        buyPrice: 0,
        currentPrice: 600,
      }],
    }
    const wrapper = mount(StockLedgerInitialization, {
      props: { hasActiveHoldings: true, loading: false, response },
    })

    expect(wrapper.text()).toContain('已建立 1 檔')
    expect(wrapper.text()).toContain('2330')
    expect(wrapper.text()).toContain('缺少買入均價')
  })
})
