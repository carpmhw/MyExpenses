import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { ApiError, api } from '../../src/api'
import StocksPage from '../../src/pages/stocks/index.vue'
import { mountWithAppProviders } from '../support/render'

async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

function createStockListResponse(hasLedger = true) {
  return {
    items: [{
      id: 1,
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
    }],
    total: 1,
    page: 1,
    pageSize: 15,
    totalEstimatedNetSellValue: 6000,
    totalEstimatedGainLoss: 1000,
  }
}

function createLedgerResponse() {
  return {
    items: [{
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
    }],
    total: 1,
    page: 1,
    pageSize: 20,
  }
}

describe('StocksPage ledger contract', () => {
  afterEach(() => vi.restoreAllMocks())

  // 驗證股票頁預設顯示持股 tab，且 active/closed 查詢只改變 includeClosed。
  it('renders holdings and ledger tabs with an active/closed toggle', async () => {
    const list = vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())

    const wrapper = mountWithAppProviders(StocksPage)
    await flushPromises()

    expect(wrapper.get('[data-testid="stock-tab-holdings"]').attributes('aria-selected')).toBe('true')
    expect(wrapper.get('[data-testid="stock-tab-ledger"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="stock-closed-toggle"]').exists()).toBe(true)
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ includeClosed: false }))

    await wrapper.get('[data-testid="stock-closed-toggle"]').trigger('click')
    await flushPromises()
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ includeClosed: true }))
  })

  // 驗證切換交易紀錄 tab 時載入 Ledger list 並顯示 signed transaction data。
  it('loads the transaction ledger only when its tab is selected', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    const ledger = vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())

    const wrapper = mountWithAppProviders(StocksPage)
    await flushPromises()
    expect(ledger).not.toHaveBeenCalled()

    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()

    expect(ledger).toHaveBeenCalledWith(expect.objectContaining({ page: 1 }))
    expect(wrapper.text()).toContain('第一筆買入')
    expect(wrapper.text()).toContain('交易紀錄')
  })

  // 驗證 stock/type/date filters 由 page 傳入 Ledger API，並將頁碼重設至第一頁。
  it('passes ledger filters and resets pagination', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    const ledger = vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())

    const wrapper = mountWithAppProviders(StocksPage)
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="ledger-type-filter"]').setValue('Sell')
    await wrapper.get('[data-testid="ledger-date-start"]').setValue('2026-01-01')
    await wrapper.get('[data-testid="ledger-date-end"]').setValue('2026-08-01')
    await flushPromises()

    expect(ledger).toHaveBeenLastCalledWith(expect.objectContaining({
      type: 'Sell',
      dateStart: '2026-01-01',
      dateEnd: '2026-08-01',
      page: 1,
    }))
  })

  // 驗證 Ledger-managed 的股數與買入均價在股票編輯表單中不可直接修改。
  it('locks ledger-managed shares and buy price in the edit form', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())

    const wrapper = mountWithAppProviders(StocksPage)
    await flushPromises()
    await wrapper.find('tbody tr').findAll('button')[0].trigger('click')
    await flushPromises()

    const shares = document.querySelector<HTMLInputElement>('input[type="number"][step="1"]')!
    const buyPrice = document.querySelector<HTMLInputElement>('input[type="number"][step="0.01"]')!
    expect(shares.disabled).toBe(true)
    expect(buyPrice.disabled).toBe(true)
    expect(document.body.textContent).toContain('股數由交易紀錄管理')
  })

  // 驗證持股頁提供新增交易入口，避免使用第二段 Stock mutation 取代 Ledger command。
  it('shows a new transaction entry point for ledger-managed holdings', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())

    const wrapper = mountWithAppProviders(StocksPage)
    await flushPromises()

    expect(wrapper.get('[data-testid="new-stock-transaction"]').exists()).toBe(true)
  })

  // 驗證新增交易透過 Ledger command 儲存，成功後同步更新交易與持股 projection。
  it('creates a ledger transaction from the stock page', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())
    const create = vi.spyOn(api.stocks.ledger, 'create').mockResolvedValue(createLedgerResponse().items[0])

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="ledger-new-transaction"]').trigger('click')
    await flushPromises()

    const setInput = (testId: string, value: string): void => {
      const input = document.body.querySelector<HTMLInputElement>(`[data-testid="${testId}"]`)
      if (!input) throw new Error(`Missing ${testId}`)
      input.value = value
      input.dispatchEvent(new Event('input', { bubbles: true }))
    }
    setInput('transaction-trade-date', '2026-08-01')
    setInput('transaction-shares', '5')
    setInput('transaction-price', '550')
    const form = document.body.querySelector<HTMLFormElement>('[data-testid="stock-transaction-form"]')
    if (!form) throw new Error('Missing transaction form')
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await flushPromises()

    expect(create).toHaveBeenCalledWith(expect.objectContaining({
      stockId: 1,
      type: 'Buy',
      tradeDate: '2026-08-01',
      shares: 5,
      price: 550,
    }))
    wrapper.unmount()
  })

  // 驗證 server InsufficientShares typed error 會留在 modal 內呈現，而非只顯示 generic 失敗。
  it('renders server transaction errors in the modal', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())
    vi.spyOn(api.stocks.ledger, 'create').mockRejectedValue(new ApiError({
      status: 409,
      code: 'InsufficientShares',
      title: null,
      detail: '賣出股數超過可用股數',
    }))

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="ledger-new-transaction"]').trigger('click')
    await flushPromises()

    const type = document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-type"]')
    if (!type) throw new Error('Missing transaction type')
    type.value = 'Sell'
    type.dispatchEvent(new Event('change', { bubbles: true }))
    const setInput = (testId: string, value: string): void => {
      const input = document.body.querySelector<HTMLInputElement>(`[data-testid="${testId}"]`)
      if (!input) throw new Error(`Missing ${testId}`)
      input.value = value
      input.dispatchEvent(new Event('input', { bubbles: true }))
    }
    setInput('transaction-shares', '5')
    setInput('transaction-price', '550')
    const form = document.body.querySelector<HTMLFormElement>('[data-testid="stock-transaction-form"]')
    if (!form) throw new Error('Missing transaction form')
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await flushPromises()

    expect(document.body.textContent).toContain('賣出股數超過可用股數')
    wrapper.unmount()
  })

  // 驗證初始化成功後重新抓取持股 projection，並保留 backend 的冪等結果摘要。
  it('initializes legacy holdings and refreshes the stock list', async () => {
    const list = vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse(false))
    const initialize = vi.spyOn(api.stocks.ledger, 'initialize').mockResolvedValue({
      initializedCount: 1,
      skippedCount: 0,
      blockingCount: 0,
      totalCount: 1,
      blockingStocks: [],
    })

    const wrapper = mountWithAppProviders(StocksPage)
    await flushPromises()
    expect(wrapper.get('[data-testid="ledger-initialization"]').exists()).toBe(true)
    await wrapper.get('[data-testid="initialize-ledger"]').trigger('click')
    await flushPromises()

    expect(initialize).toHaveBeenCalledWith(expect.objectContaining({ baselineDate: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/) }))
    expect(list).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).toContain('已建立 1 檔')
  })
})
