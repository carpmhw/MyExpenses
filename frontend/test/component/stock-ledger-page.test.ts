import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { ApiError, api } from '../../src/api'
import StocksPage from '../../src/pages/stocks/index.vue'
import StockTransactionLedger from '../../src/components/stocks/StockTransactionLedger.vue'
import type { StockListItem, StockOption, StockTransactionListItem } from '../../src/types'
import { mountWithAppProviders } from '../support/render'
import { deferred } from '../support/deferred'

// 等待 Vue watcher 與非同步 API mock 完成，讓 Teleport 內容穩定後再斷言。
async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

// 建立可指定識別欄位的持股 fixture，讓回歸測試能區分股票 A 與 B。
function createStockListItem(
  id: number,
  name: string,
  symbol: string,
  overrides: Partial<StockListItem> = {},
): StockListItem {
  return {
    id,
    name,
    symbol,
    market: 'Twse',
    instrumentType: 'Stock',
    shares: id === 1 ? 10 : 20,
    buyPrice: id === 1 ? 500 : 700,
    currentPrice: id === 1 ? 600 : 800,
    broker: id === 1 ? '甲券商' : '乙券商',
    lastPriceUpdate: null,
    grossMarketValue: id === 1 ? 6000 : 16000,
    buyCommission: 0,
    sellCommission: 0,
    securitiesTransactionTax: 0,
    estimatedNetSellValue: id === 1 ? 6000 : 16000,
    estimatedGainLoss: id === 1 ? 1000 : 2000,
    hasLedger: true,
    ...overrides,
  }
}

// 建立股票頁測試用持股 response，允許覆寫 projection 與 identity 欄位。
function createStockListResponse(hasLedger = true, overrides: Partial<StockListItem> = {}) {
  return {
    items: [createStockListItem(1, '台積電', '2330', { hasLedger, ...overrides })],
    total: 1,
    page: 1,
    pageSize: 15,
    totalEstimatedNetSellValue: 6000,
    totalEstimatedGainLoss: 1000,
  }
}

// 建立兩檔股票 fixture，持股首筆順序可與完整 options 順序刻意錯開。
function createTwoStockFixture(firstHoldingId: 1 | 2 = 1): {
  stocks: ReturnType<typeof createStockListResponse>
  options: StockOption[]
} {
  const stockA = createStockListItem(1, '台積電', '2330')
  const stockB = createStockListItem(2, '聯發科', '2454')
  const items = firstHoldingId === 1 ? [stockA, stockB] : [stockB, stockA]
  return {
    stocks: {
      items,
      total: 2,
      page: 1,
      pageSize: 15,
      totalEstimatedNetSellValue: 22000,
      totalEstimatedGainLoss: 3000,
    },
    options: [stockA, stockB].map(stock => ({
      id: stock.id,
      name: stock.name,
      symbol: stock.symbol,
      broker: stock.broker,
      shares: stock.shares,
      hasLedger: stock.hasLedger,
    })),
  }
}

// 建立沒有股票的 response，供新增交易無有效 Stock ID 的 guard 測試使用。
function createEmptyStockListResponse() {
  return {
    ...createStockListResponse(),
    items: [],
    total: 0,
    totalEstimatedNetSellValue: 0,
    totalEstimatedGainLoss: 0,
  }
}

// 建立股票交易紀錄 fixture，允許測試覆寫既有交易的股票與型別。
function createLedgerResponse(overrides: Partial<StockTransactionListItem> = {}) {
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
      ...overrides,
    }],
    total: 1,
    page: 1,
    pageSize: 20,
  }
}

// 關閉目前 Teleport 到 body 的交易 Modal，避免後續入口受到前一個表單影響。
function closeTransactionModal(): void {
  const form = document.body.querySelector<HTMLFormElement>('[data-testid="stock-transaction-form"]')
  const cancelButton = Array.from(form?.querySelectorAll<HTMLButtonElement>('button') ?? [])
    .find(button => button.textContent?.trim() === '取消')
  if (!cancelButton) throw new Error('Missing transaction cancel button')
  cancelButton.click()
}

// 以原生 input event 設定交易表單欄位，符合既有 component test 的輸入慣例。
function setTransactionInput(testId: string, value: string): void {
  const input = document.body.querySelector<HTMLInputElement>(`[data-testid="${testId}"]`)
  if (!input) throw new Error(`Missing ${testId}`)
  input.value = value
  input.dispatchEvent(new Event('input', { bubbles: true }))
}

// 填入不依賴費稅估算服務的有效買入交易資料。
function fillManualBuyTransaction(): void {
  const manualButton = document.body.querySelector<HTMLButtonElement>('[data-testid="transaction-cost-manual"]')
  if (!manualButton) throw new Error('Missing manual transaction cost button')
  manualButton.click()
  setTransactionInput('transaction-shares', '2')
  setTransactionInput('transaction-price', '610')
  setTransactionInput('transaction-fee', '0')
  setTransactionInput('transaction-tax', '0')
}

describe('StocksPage ledger contract', () => {
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  // 驗證持股頁的新 Sell 流程會透過 central API client 取得自動費稅。
  it('estimates a new sell transaction from the stock page', async () => {
    vi.useFakeTimers()
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    vi.spyOn(api.stocks, 'options').mockResolvedValue([{
      id: 1,
      name: '台積電',
      symbol: '2330',
      broker: '甲券商',
      shares: 10,
      hasLedger: true,
    }])
    const estimate = vi.spyOn(api.stocks.ledger, 'estimateCosts').mockResolvedValue({
      grossAmount: 1220,
      fee: 20,
      tax: 3,
    })

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-sell-1"]').trigger('click')
    await flushPromises()

    const setInput = (testId: string, value: string): void => {
      const input = document.body.querySelector<HTMLInputElement>(`[data-testid="${testId}"]`)
      if (!input) throw new Error(`Missing ${testId}`)
      input.value = value
      input.dispatchEvent(new Event('input', { bubbles: true }))
    }
    setInput('transaction-shares', '2')
    setInput('transaction-price', '610')
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()

    expect(estimate).toHaveBeenCalledWith(
      { stockId: 1, type: 'Sell', shares: 2, price: 610 },
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    )
    expect((document.body.querySelector<HTMLInputElement>('[data-testid="transaction-fee"]')!).value).toBe('20')
    expect((document.body.querySelector<HTMLInputElement>('[data-testid="transaction-tax"]')!).value).toBe('3')
    wrapper.unmount()
  })

  // 驗證自動估算失敗後切換 manual 仍可用實際費稅送出交易。
  it('allows manual override after an estimate failure on the stock page', async () => {
    vi.useFakeTimers()
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    vi.spyOn(api.stocks, 'options').mockResolvedValue([{
      id: 1,
      name: '台積電',
      symbol: '2330',
      broker: '甲券商',
      shares: 10,
      hasLedger: true,
    }])
    vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())
    const estimate = vi.spyOn(api.stocks.ledger, 'estimateCosts').mockRejectedValue(new ApiError({
      status: 500,
      userMessage: '估算服務暫時失敗',
    }))
    const create = vi.spyOn(api.stocks.ledger, 'create').mockResolvedValue(createLedgerResponse().items[0])

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-buy-1"]').trigger('click')
    await flushPromises()
    const setInput = (testId: string, value: string): void => {
      const input = document.body.querySelector<HTMLInputElement>(`[data-testid="${testId}"]`)
      if (!input) throw new Error(`Missing ${testId}`)
      input.value = value
      input.dispatchEvent(new Event('input', { bubbles: true }))
    }
    setInput('transaction-shares', '2')
    setInput('transaction-price', '610')
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()
    expect(estimate).toHaveBeenCalledTimes(1)
    expect(document.body.textContent).toContain('估算服務暫時失敗')

    document.body.querySelector<HTMLButtonElement>('[data-testid="transaction-cost-manual"]')?.click()
    setInput('transaction-fee', '18')
    setInput('transaction-tax', '4')
    document.body.querySelector<HTMLFormElement>('[data-testid="stock-transaction-form"]')
      ?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await flushPromises()

    expect(create).toHaveBeenCalledWith(expect.objectContaining({ fee: 18, tax: 4 }))
    wrapper.unmount()
  })

  // 驗證 options 初次載入未完成時，交易 Modal 收到 loading freshness 狀態而不顯示暫時股數。
  it('passes loading stock options status to the transaction modal', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    const pendingOptions = deferred<Awaited<ReturnType<typeof api.stocks.options>>>()
    vi.spyOn(api.stocks, 'options').mockReturnValue(pendingOptions.promise)

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="new-stock-transaction"]').trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('目前持股載入中')
    expect(document.body.textContent).not.toContain('目前持有 10 股')
    wrapper.unmount()
  })

  // 驗證 options 失敗狀態會傳入交易 Modal，而不使用失敗前保留的股數。
  it('passes error stock options status to the transaction modal', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    vi.spyOn(api.stocks, 'options').mockRejectedValue(new Error('options unavailable'))

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="new-stock-transaction"]').trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('目前持股暫時無法取得')
    expect(document.body.textContent).not.toContain('目前持有 10 股')
    wrapper.unmount()
  })

  // 驗證 options 失敗後切換持股分頁，新增交易仍保留目前 clicked stock 的 identity。
  it('keeps the clicked stock identity after an options failure and page change', async () => {
    const firstPage = { ...createStockListResponse(), total: 16 }
    const secondPage = {
      ...createStockListResponse(true, { id: 2, name: '聯發科', symbol: '2454' }),
      page: 2,
      total: 16,
    }
    vi.spyOn(api.stocks, 'list')
      .mockResolvedValueOnce(firstPage)
      .mockResolvedValueOnce(secondPage)
    vi.spyOn(api.stocks, 'options').mockRejectedValue(new Error('options unavailable'))

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-buy-1"]').trigger('click')
    await flushPromises()

    const cancelButton = Array.from(document.body.querySelectorAll<HTMLButtonElement>('button'))
      .find(button => button.textContent?.trim() === '取消')
    cancelButton?.click()
    await flushPromises()

    const nextButton = Array.from(document.body.querySelectorAll<HTMLButtonElement>('button'))
      .find(button => button.textContent?.includes('下一頁'))
    if (!nextButton) throw new Error('Missing next page button')
    nextButton.click()
    await flushPromises()

    await wrapper.get('[data-testid="stock-buy-2"]').trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('聯發科')
    wrapper.unmount()
  })

  // 驗證 ready options 缺少 clicked stock 時仍保留 identity，但不推測目前持股。
  it('keeps identity without current shares when ready options omit the clicked stock', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    vi.spyOn(api.stocks, 'options').mockResolvedValue([])

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-buy-1"]').trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('台積電')
    expect(document.body.textContent).not.toContain('目前持有 10 股')
    wrapper.unmount()
  })

  // 驗證 Ledger mutation 啟動 refresh 後，舊 options 在新 response 前不再具有 ready freshness。
  it('invalidates ready stock options freshness during a ledger refresh', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())
    vi.spyOn(api.stocks.ledger, 'create').mockResolvedValue(createLedgerResponse().items[0])
    const refreshedOptions = deferred<Awaited<ReturnType<typeof api.stocks.options>>>()
    vi.spyOn(api.stocks, 'options')
      .mockResolvedValueOnce([{ id: 1, name: '台積電', symbol: '2330', broker: '甲券商', shares: 10, hasLedger: true }])
      .mockReturnValueOnce(refreshedOptions.promise)

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="new-stock-transaction"]').trigger('click')
    await flushPromises()
    const cancelButton = Array.from(document.body.querySelectorAll<HTMLButtonElement>('button'))
      .find(button => button.textContent?.trim() === '取消')
    cancelButton?.click()
    await flushPromises()

    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="ledger-new-transaction"]').trigger('click')
    await flushPromises()
    const form = document.body.querySelector<HTMLFormElement>('[data-testid="stock-transaction-form"]')
    if (!form) throw new Error('Missing transaction form')
    const setInput = (testId: string, value: string): void => {
      const input = document.body.querySelector<HTMLInputElement>(`[data-testid="${testId}"]`)
      if (!input) throw new Error(`Missing ${testId}`)
      input.value = value
      input.dispatchEvent(new Event('input', { bubbles: true }))
    }
    setInput('transaction-shares', '2')
    setInput('transaction-price', '610')
    document.body.querySelector<HTMLButtonElement>('[data-testid="transaction-cost-manual"]')?.click()
    setInput('transaction-fee', '0')
    setInput('transaction-tax', '0')
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await flushPromises()

    await wrapper.get('[data-testid="ledger-new-transaction"]').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('目前持股載入中')
    expect(document.body.textContent).not.toContain('目前持有 10 股')
    refreshedOptions.resolve([{ id: 1, name: '台積電', symbol: '2330', broker: '甲券商', shares: 8, hasLedger: true }])
    await flushPromises()
    wrapper.unmount()
  })

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

  // 驗證點擊持股列會重設分頁，並只查詢一次該股票的交易紀錄。
  it('opens the filtered transaction ledger from a holding row', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    const ledger = vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue({
      ...createLedgerResponse(),
      total: 21,
    })

    const wrapper = mountWithAppProviders(StocksPage)
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="ledger-type-filter"]').setValue('Sell')
    await wrapper.get('[data-testid="ledger-date-start"]').setValue('2026-01-01')
    await wrapper.get('[data-testid="ledger-date-end"]').setValue('2026-08-01')
    await flushPromises()
    await wrapper.get('[data-testid="ledger-page-next"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-holdings"]').trigger('click')
    ledger.mockClear()

    await wrapper.get('[data-testid="stock-row-1"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="stock-tab-ledger"]').attributes('aria-selected')).toBe('true')
    expect(wrapper.get<HTMLSelectElement>('[data-testid="ledger-stock-filter"]').element.value).toBe('1')
    expect(ledger).toHaveBeenCalledTimes(1)
    expect(ledger).toHaveBeenCalledWith(expect.objectContaining({
      stockId: 1,
      type: 'Sell',
      dateStart: '2026-01-01',
      dateEnd: '2026-08-01',
      page: 1,
    }))

    await wrapper.get('[data-testid="ledger-stock-filter"]').setValue('')
    await flushPromises()

    expect(ledger).toHaveBeenCalledTimes(2)
    expect(ledger).toHaveBeenLastCalledWith(expect.objectContaining({
      stockId: undefined,
      type: 'Sell',
      dateStart: '2026-01-01',
      dateEnd: '2026-08-01',
      page: 1,
    }))
  })

  // 驗證股票名稱提供原生鍵盤控制項，且啟動後只查詢一次交易紀錄。
  it('opens the filtered transaction ledger from the stock name control', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    const ledger = vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())

    const wrapper = mountWithAppProviders(StocksPage)
    await flushPromises()
    const stockNameControl = wrapper.get('[data-testid="stock-ledger-1"]')

    expect(stockNameControl.element.tagName).toBe('BUTTON')
    await stockNameControl.trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="stock-tab-ledger"]').attributes('aria-selected')).toBe('true')
    expect(wrapper.get<HTMLSelectElement>('[data-testid="ledger-stock-filter"]').element.value).toBe('1')
    expect(ledger).toHaveBeenCalledTimes(1)
    expect(ledger).toHaveBeenCalledWith(expect.objectContaining({ stockId: 1, page: 1 }))
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

  // 驗證股票股利出現在 Ledger filter，且選取後會傳送正確型別給 API。
  it('passes the stock dividend ledger filter', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    const ledger = vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())

    const wrapper = mountWithAppProviders(StocksPage)
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()

    const filter = wrapper.get('[data-testid="ledger-type-filter"]')
    expect(filter.text()).toContain('股票股利')
    await filter.setValue('StockDividend')
    await flushPromises()

    expect(ledger).toHaveBeenLastCalledWith(expect.objectContaining({ type: 'StockDividend', page: 1 }))
  })

  // 驗證 Ledger-managed 的股數與買入均價在股票編輯表單中不可直接修改。
  it('locks ledger-managed shares and buy price in the edit form', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    const ledger = vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())

    const wrapper = mountWithAppProviders(StocksPage)
    await flushPromises()
    await wrapper.get('[data-testid="stock-edit-1"]').trigger('click')
    await flushPromises()

    const shares = document.querySelector<HTMLInputElement>('input[type="number"][step="1"]')!
    const buyPrice = document.querySelector<HTMLInputElement>('input[type="number"][step="0.01"]')!
    expect(shares.disabled).toBe(true)
    expect(buyPrice.disabled).toBe(true)
    expect(document.body.textContent).toContain('股數由交易紀錄管理')
    expect(wrapper.get('[data-testid="stock-tab-holdings"]').attributes('aria-selected')).toBe('true')
    expect(ledger).not.toHaveBeenCalled()
  })

  // 驗證已結清的 Ledger 持股仍可編輯名稱與現價，不被零 projection 驗證阻擋。
  it('allows editing metadata for a closed ledger-managed holding', async () => {
    const closedStock = createStockListResponse(true, { shares: 0, buyPrice: 0, currentPrice: 0 })
    vi.spyOn(api.stocks, 'list').mockResolvedValue(closedStock)
    vi.spyOn(api.stocks, 'lookup').mockResolvedValue({
      name: null,
      currentPrice: null,
      market: 'Twse',
      resultCode: 'Completed',
    })
    const update = vi.spyOn(api.stocks, 'update').mockResolvedValue({} as Awaited<ReturnType<typeof api.stocks.update>>)

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-edit-1"]').trigger('click')
    await flushPromises()

    const form = document.body.querySelector<HTMLFormElement>('form')!
    const nameInput = form.querySelector<HTMLInputElement>('input[placeholder="e.g. 台積電"]')!
    nameInput.value = '已結清台積電'
    nameInput.dispatchEvent(new Event('input', { bubbles: true }))
    const priceInput = form.querySelectorAll<HTMLInputElement>('input[type="number"][step="0.01"]')[1]
    priceInput.value = '620'
    priceInput.dispatchEvent(new Event('input', { bubbles: true }))
    expect(form.querySelector<HTMLInputElement>('input[type="number"][step="1"]')!.disabled).toBe(true)
    form.querySelector<HTMLInputElement>('#syncPrice')!.click()
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await flushPromises()

    expect(update).toHaveBeenCalledWith(1, expect.objectContaining({ name: '已結清台積電', currentPrice: 620 }))
    expect(update.mock.calls[0]?.[1]).not.toHaveProperty('shares')
    expect(update.mock.calls[0]?.[1]).not.toHaveProperty('buyPrice')
    wrapper.unmount()
  })

  // 驗證 Ledger-managed 股票的身份欄位鎖定，已知市場也不可直接切換。
  it('locks Ledger-managed identity fields and known market', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-edit-1"]').trigger('click')
    await flushPromises()

    const form = document.body.querySelector<HTMLFormElement>('form')!
    expect(form.querySelector<HTMLInputElement>('input[placeholder="e.g. 2330"]')!.disabled).toBe(true)
    expect(form.querySelector<HTMLInputElement>('input[placeholder="e.g. 元大證券"]')!.disabled).toBe(true)
    expect(form.querySelector<HTMLSelectElement>('[data-testid="stock-edit-instrument-type"]')!.disabled).toBe(true)
    expect(form.querySelector<HTMLSelectElement>('[data-testid="stock-edit-market"]')!.disabled).toBe(true)
    wrapper.unmount()
  })

  // 驗證持股頁提供新增交易入口，避免使用第二段 Stock mutation 取代 Ledger command。
  it('shows a new transaction entry point for ledger-managed holdings', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())

    const wrapper = mountWithAppProviders(StocksPage)
    await flushPromises()

    expect(wrapper.get('[data-testid="new-stock-transaction"]').exists()).toBe(true)
  })

  // 驗證 Ledger 篩選非第一筆股票時，Ledger 內新增入口預選篩選標的並維持 Buy。
  it('defaults a ledger-created transaction to the filtered stock', async () => {
    const fixture = createTwoStockFixture()
    vi.spyOn(api.stocks, 'list').mockResolvedValue(fixture.stocks)
    vi.spyOn(api.stocks, 'options').mockResolvedValue(fixture.options)
    vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse({
      stockId: 2,
      stockName: '聯發科',
      symbol: '2454',
      broker: '乙券商',
    }))

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="ledger-stock-filter"]').setValue('2')
    await wrapper.get('[data-testid="ledger-date-start"]').setValue('2026-01-01')
    await wrapper.get('[data-testid="ledger-date-end"]').setValue('2026-08-01')
    await flushPromises()
    await wrapper.get('[data-testid="ledger-new-transaction"]').trigger('click')
    await flushPromises()

    expect(document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-stock"]')?.value).toBe('2')
    expect(document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-type"]')?.value).toBe('Buy')
    expect(document.body.querySelector<HTMLInputElement>('[data-testid="transaction-trade-date"]')?.value).not.toBe('2026-08-01')
    wrapper.unmount()
  })

  // 驗證頁首新增入口在 Ledger 篩選後與 Ledger 內入口使用相同的股票預選規則。
  it('defaults the header transaction entry to the filtered stock', async () => {
    const fixture = createTwoStockFixture()
    vi.spyOn(api.stocks, 'list').mockResolvedValue(fixture.stocks)
    vi.spyOn(api.stocks, 'options').mockResolvedValue(fixture.options)
    vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse({
      stockId: 2,
      stockName: '聯發科',
      symbol: '2454',
      broker: '乙券商',
    }))

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="ledger-stock-filter"]').setValue('2')
    await flushPromises()
    await wrapper.get('[data-testid="new-stock-transaction"]').trigger('click')
    await flushPromises()

    expect(document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-stock"]')?.value).toBe('2')
    expect(document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-type"]')?.value).toBe('Buy')
    wrapper.unmount()
  })

  // 驗證 Ledger 選擇全部股票時，已載入的完整 options 首筆優先於持股列表首筆。
  it('prefers the first loaded stock option over the first holding', async () => {
    const fixture = createTwoStockFixture(2)
    vi.spyOn(api.stocks, 'list').mockResolvedValue(fixture.stocks)
    vi.spyOn(api.stocks, 'options').mockResolvedValue(fixture.options)
    vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="new-stock-transaction"]').trigger('click')
    await flushPromises()
    closeTransactionModal()
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="new-stock-transaction"]').trigger('click')
    await flushPromises()

    expect(document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-stock"]')?.value).toBe('1')
    wrapper.unmount()
  })

  // 驗證完整 options 尚無項目時，新增交易會回退目前持股列表首筆股票。
  it('falls back to the first holding when stock options are empty', async () => {
    const fixture = createTwoStockFixture()
    const options = vi.spyOn(api.stocks, 'options').mockResolvedValue([])
    vi.spyOn(api.stocks, 'list').mockResolvedValue(fixture.stocks)

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="new-stock-transaction"]').trigger('click')
    await flushPromises()
    closeTransactionModal()
    await flushPromises()
    await wrapper.get('[data-testid="new-stock-transaction"]').trigger('click')
    await flushPromises()

    expect(options).toHaveBeenCalledTimes(1)
    expect(document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-stock"]')?.value).toBe('1')
    wrapper.unmount()
  })

  // 驗證股票與 options 都沒有 ID 時，Ledger create event 不會開啟交易 Modal。
  it('does not open a transaction modal without any stock id', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createEmptyStockListResponse())
    vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue({
      ...createLedgerResponse(),
      items: [],
      total: 0,
    })

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()
    wrapper.findComponent(StockTransactionLedger).vm.$emit('create')
    await flushPromises()

    expect(document.body.querySelector('[data-testid="stock-transaction-form"]')).toBeNull()
    expect(document.body.querySelector('[data-testid="transaction-stock"]')).toBeNull()
    wrapper.unmount()
  })

  // 驗證切回 Holdings 後頁首新增回復 options fallback，且不清空保留的 Ledger 股票篩選。
  it('ignores the retained ledger filter for the holdings header entry', async () => {
    const fixture = createTwoStockFixture()
    vi.spyOn(api.stocks, 'list').mockResolvedValue(fixture.stocks)
    vi.spyOn(api.stocks, 'options').mockResolvedValue(fixture.options)
    vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse({
      stockId: 2,
      stockName: '聯發科',
      symbol: '2454',
      broker: '乙券商',
    }))

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="ledger-stock-filter"]').setValue('2')
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-holdings"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="new-stock-transaction"]').trigger('click')
    await flushPromises()

    expect(document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-stock"]')?.value).toBe('1')
    closeTransactionModal()
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()
    expect(wrapper.get<HTMLSelectElement>('[data-testid="ledger-stock-filter"]').element.value).toBe('2')
    wrapper.unmount()
  })

  // 驗證持股列明確指定的股票與 Buy/Sell 型別優先於殘留 Ledger 篩選。
  it('preserves explicit holding stock and transaction type after a ledger filter', async () => {
    const fixture = createTwoStockFixture()
    vi.spyOn(api.stocks, 'list').mockResolvedValue(fixture.stocks)
    vi.spyOn(api.stocks, 'options').mockResolvedValue(fixture.options)
    vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse({
      stockId: 2,
      stockName: '聯發科',
      symbol: '2454',
      broker: '乙券商',
    }))

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="ledger-stock-filter"]').setValue('2')
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-holdings"]').trigger('click')
    await flushPromises()

    await wrapper.get('[data-testid="stock-buy-1"]').trigger('click')
    await flushPromises()
    expect(document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-stock"]')?.value).toBe('1')
    expect(document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-type"]')?.value).toBe('Buy')
    closeTransactionModal()
    await flushPromises()

    await wrapper.get('[data-testid="stock-sell-1"]').trigger('click')
    await flushPromises()
    expect(document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-stock"]')?.value).toBe('1')
    expect(document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-type"]')?.value).toBe('Sell')
    wrapper.unmount()
  })

  // 驗證使用者手動改選股票後，非同步更新不會將選擇覆寫回 Ledger 篩選。
  it('keeps a manually changed stock and submits its id', async () => {
    const fixture = createTwoStockFixture()
    const create = vi.spyOn(api.stocks.ledger, 'create').mockResolvedValue(createLedgerResponse().items[0])
    vi.spyOn(api.stocks, 'list').mockResolvedValue(fixture.stocks)
    vi.spyOn(api.stocks, 'options').mockResolvedValue(fixture.options)
    vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse({
      stockId: 2,
      stockName: '聯發科',
      symbol: '2454',
      broker: '乙券商',
    }))

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="ledger-stock-filter"]').setValue('2')
    await flushPromises()
    await wrapper.get('[data-testid="new-stock-transaction"]').trigger('click')
    await flushPromises()
    const stockSelect = document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-stock"]')
    if (!stockSelect) throw new Error('Missing transaction stock select')
    stockSelect.value = '1'
    stockSelect.dispatchEvent(new Event('change', { bubbles: true }))
    await flushPromises()

    expect(document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-stock"]')?.value).toBe('1')
    fillManualBuyTransaction()
    const form = document.body.querySelector<HTMLFormElement>('[data-testid="stock-transaction-form"]')
    if (!form) throw new Error('Missing transaction form')
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await flushPromises()

    expect(create).toHaveBeenCalledWith(expect.objectContaining({ stockId: 1, type: 'Buy' }))
    expect(wrapper.get<HTMLSelectElement>('[data-testid="ledger-stock-filter"]').element.value).toBe('2')
    wrapper.unmount()
  })

  // 驗證 Ledger 篩選脈絡中的既有交易編輯仍使用交易自身的股票與型別。
  it('keeps an edited transaction stock instead of applying the new default', async () => {
    const fixture = createTwoStockFixture()
    vi.spyOn(api.stocks, 'list').mockResolvedValue(fixture.stocks)
    vi.spyOn(api.stocks, 'options').mockResolvedValue(fixture.options)
    vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse({
      stockId: 2,
      stockName: '聯發科',
      symbol: '2454',
      broker: '乙券商',
    }))

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-tab-ledger"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="ledger-stock-filter"]').setValue('2')
    await flushPromises()
    // 保留真實 B 列表，再透過既有 edit event 注入 A 交易以區分新增預設與編輯資料。
    wrapper.findComponent(StockTransactionLedger).vm.$emit('edit', createLedgerResponse({ stockId: 1, type: 'Sell' }).items[0])
    await flushPromises()

    expect(document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-stock"]')?.value).toBe('1')
    expect(document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-type"]')?.value).toBe('Sell')
    expect(wrapper.get<HTMLSelectElement>('[data-testid="ledger-stock-filter"]').element.value).toBe('2')
    wrapper.unmount()
  })

  // 驗證持股列 Buy 快捷操作預選正確 StockId 與交易型別。
  it('opens a Buy transaction from the holding row', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    const ledger = vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())
    vi.spyOn(api.stocks, 'options').mockResolvedValue([{
      id: 1,
      name: '台積電',
      symbol: '2330',
      broker: '甲券商',
      shares: 10,
      hasLedger: true,
    }])

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-buy-1"]').trigger('click')
    await flushPromises()

    expect((document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-stock"]')!).value).toBe('1')
    expect((document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-type"]')!).value).toBe('Buy')
    expect(wrapper.get('[data-testid="stock-tab-holdings"]').attributes('aria-selected')).toBe('true')
    expect(ledger).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  // 驗證持股列 Sell 快捷操作預選正確 StockId、交易型別與券商提示。
  it('opens a Sell transaction from the holding row', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    const ledger = vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())
    vi.spyOn(api.stocks, 'options').mockResolvedValue([{
      id: 1,
      name: '台積電',
      symbol: '2330',
      broker: '甲券商',
      shares: 10,
      hasLedger: true,
    }])

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="stock-sell-1"]').trigger('click')
    await flushPromises()

    expect((document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-stock"]')!).value).toBe('1')
    expect((document.body.querySelector<HTMLSelectElement>('[data-testid="transaction-type"]')!).value).toBe('Sell')
    expect(document.body.textContent).toContain('2330 台積電｜甲券商')
    expect(wrapper.get('[data-testid="stock-tab-holdings"]').attributes('aria-selected')).toBe('true')
    expect(ledger).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  // 驗證全域新增交易使用完整 options，不受目前持股列表 15 筆分頁限制。
  it('loads stock options beyond the current holdings page', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    const options = Array.from({ length: 40 }, (_, index) => ({
      id: index + 1,
      name: `標的 ${index + 1}`,
      symbol: String(index + 1).padStart(4, '0'),
      broker: index % 2 === 0 ? '元大證券' : '富邦證券',
      shares: 10,
      hasLedger: true,
    }))
    const loadOptions = vi.spyOn(api.stocks, 'options').mockResolvedValue(options)

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="new-stock-transaction"]').trigger('click')
    await flushPromises()

    expect(loadOptions).toHaveBeenCalledWith({ includeClosed: true })
    expect(document.body.textContent).toContain('0040 標的 40｜富邦證券')
    wrapper.unmount()
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
    document.body.querySelector<HTMLButtonElement>('[data-testid="transaction-cost-manual"]')?.click()
    setInput('transaction-fee', '0')
    setInput('transaction-tax', '0')
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
    document.body.querySelector<HTMLButtonElement>('[data-testid="transaction-cost-manual"]')?.click()
    setInput('transaction-fee', '0')
    setInput('transaction-tax', '0')
    const form = document.body.querySelector<HTMLFormElement>('[data-testid="stock-transaction-form"]')
    if (!form) throw new Error('Missing transaction form')
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await flushPromises()

    expect(document.body.textContent).toContain('賣出股數超過可用股數')
    wrapper.unmount()
  })

  // 驗證持股列的 Ledger delete control 不可執行，避免送出必然被 backend 拒絕的 request。
  it('disables deletion for Ledger-managed holdings', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    const ledger = vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()

    const deleteButton = wrapper.find('[data-testid="stock-delete-1"]')
    expect(deleteButton.attributes('disabled')).toBeDefined()
    await deleteButton.trigger('click')
    expect(document.body.textContent).not.toContain('確定要刪除此股票記錄嗎？')
    expect(wrapper.get('[data-testid="stock-tab-holdings"]').attributes('aria-selected')).toBe('true')
    expect(ledger).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  // 驗證沒有 Ledger 的 legacy 持股仍可開啟既有刪除確認流程。
  it('keeps deletion available for legacy holdings', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse(false))
    const ledger = vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()

    await wrapper.find('[data-testid="stock-delete-1"]').trigger('click')
    expect(document.body.textContent).toContain('確定要刪除此股票記錄嗎？')
    expect(wrapper.get('[data-testid="stock-tab-holdings"]').attributes('aria-selected')).toBe('true')
    expect(ledger).not.toHaveBeenCalled()
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

  // 驗證首次 options 載入尚未完成時，Ledger mutation 仍會啟動較新的 options refresh。
  it('refreshes stock options after a ledger mutation races the initial options load', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue(createStockListResponse())
    vi.spyOn(api.stocks.ledger, 'list').mockResolvedValue(createLedgerResponse())
    vi.spyOn(api.stocks.ledger, 'create').mockResolvedValue(createLedgerResponse().items[0])
    const initialOptions = deferred<Awaited<ReturnType<typeof api.stocks.options>>>()
    const refreshedOptions = deferred<Awaited<ReturnType<typeof api.stocks.options>>>()
    const options = vi.spyOn(api.stocks, 'options')
      .mockReturnValueOnce(initialOptions.promise)
      .mockReturnValueOnce(refreshedOptions.promise)

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
    setInput('transaction-shares', '2')
    setInput('transaction-price', '610')
    document.body.querySelector<HTMLButtonElement>('[data-testid="transaction-cost-manual"]')?.click()
    setInput('transaction-fee', '0')
    setInput('transaction-tax', '0')
    document.body.querySelector<HTMLFormElement>('[data-testid="stock-transaction-form"]')
      ?.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await flushPromises()

    expect(options).toHaveBeenCalledTimes(2)
    refreshedOptions.resolve([{ id: 1, name: '台積電', symbol: '2330', broker: '甲券商', shares: 8, hasLedger: true }])
    await flushPromises()
    initialOptions.resolve([{ id: 1, name: '台積電', symbol: '2330', broker: '甲券商', shares: 10, hasLedger: true }])
    await flushPromises()

    await wrapper.get('[data-testid="ledger-new-transaction"]').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('[data-testid="transaction-stock-summary"]')?.textContent).toContain('目前持有 8 股')
    wrapper.unmount()
  })

  // 驗證刪除 legacy 股票後，已載入的 options cache 不會保留已刪除 StockId。
  it('refreshes stock options after deleting a legacy holding', async () => {
    const initialResponse = createStockListResponse(false)
    const emptyResponse = { ...initialResponse, items: [], total: 0 }
    vi.spyOn(api.stocks, 'list')
      .mockResolvedValueOnce(initialResponse)
      .mockResolvedValueOnce(emptyResponse)
    const options = vi.spyOn(api.stocks, 'options')
      .mockResolvedValueOnce([{ id: 1, name: '台積電', symbol: '2330', broker: '甲券商', shares: 10, hasLedger: false }])
      .mockResolvedValueOnce([])
    const remove = vi.spyOn(api.stocks, 'delete').mockResolvedValue(undefined)

    const wrapper = mountWithAppProviders(StocksPage, { attachTo: document.body })
    await flushPromises()
    await wrapper.get('[data-testid="new-stock-transaction"]').trigger('click')
    await flushPromises()
    const cancelButton = Array.from(document.body.querySelectorAll<HTMLButtonElement>('button'))
      .find(button => button.textContent?.trim() === '取消')
    cancelButton?.click()
    await flushPromises()
    await wrapper.get('[data-testid="stock-delete-1"]').trigger('click')
    const confirmButton = Array.from(document.body.querySelectorAll<HTMLButtonElement>('button'))
      .find(button => button.textContent?.trim() === '確認刪除')
    confirmButton?.click()
    await flushPromises()

    expect(remove).toHaveBeenCalledWith(1)
    expect(options).toHaveBeenCalledTimes(2)
    wrapper.unmount()
  })
})
