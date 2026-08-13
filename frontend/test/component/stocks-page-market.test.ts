import { afterEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { mount } from '@vue/test-utils'
import { api } from '../../src/api'
import StocksPage from '../../src/pages/stocks/index.vue'
import { mountWithAppProviders } from '../support/render'
import { deferred } from '../support/deferred'

async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

describe('StocksPage market contract', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.useRealTimers()
  })

  it('shows the market label in the list and all market choices in the form', async () => {
    vi.spyOn(api.stocks, 'list').mockResolvedValue({
      items: [{
        id: 1,
        name: '台積電',
        symbol: '2330',
        market: 'Twse',
        instrumentType: 'Stock',
        shares: 10,
        buyPrice: 500,
        currentPrice: 600,
        broker: null,
        lastPriceUpdate: null,
        grossMarketValue: 6000,
        buyCommission: 0,
        sellCommission: 0,
        securitiesTransactionTax: 0,
        estimatedNetSellValue: 6000,
        estimatedGainLoss: 1000,
      }],
      total: 1,
      page: 1,
      pageSize: 15,
      totalEstimatedNetSellValue: 6000,
      totalEstimatedGainLoss: 1000,
    })

    const wrapper = mountWithAppProviders(StocksPage)
    await flushPromises()

    expect(wrapper.text()).toContain('上市')
    const createButton = wrapper.findAll('button').find(button => button.text().includes('新增股票'))
    expect(createButton).toBeDefined()
    await createButton!.trigger('click')
    expect(document.body.textContent).toContain('待辨識')
    expect(document.body.textContent).toContain('上櫃')
  })

  it('applies a unique lookup market, name, and current price to a new stock', async () => {
    vi.useFakeTimers()
    vi.spyOn(api.stocks, 'list').mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 15,
      totalEstimatedNetSellValue: 0,
      totalEstimatedGainLoss: 0,
    })
    vi.spyOn(api.stocks, 'lookup').mockResolvedValue({
      name: '台積電',
      currentPrice: 1000,
      market: 'Twse',
      resultCode: 'Completed',
    })

    const wrapper = mountWithAppProviders(StocksPage)
    const createButton = wrapper.findAll('button').find(button => button.text().includes('新增股票'))
    await createButton!.trigger('click')
    await flushPromises()
    const symbolInput = document.querySelector<HTMLInputElement>('input[placeholder="e.g. 2330"]')!
    symbolInput.value = '2330'
    symbolInput.dispatchEvent(new Event('input'))
    await vi.advanceTimersByTimeAsync(400)
    await flushPromises()

    expect(document.querySelector<HTMLInputElement>('input[placeholder="e.g. 台積電"]')!.value).toBe('台積電')
    expect(document.querySelector<HTMLSelectElement>('select')!.value).toBe('Twse')
    expect(document.querySelectorAll<HTMLInputElement>('input[type="number"][step="0.01"]')[1].value).toBe('1000')
  })

  it('does not apply a late lookup response to a newer symbol or manually selected market', async () => {
    vi.useFakeTimers()
    vi.spyOn(api.stocks, 'list').mockResolvedValue({
      items: [],
      total: 0,
      page: 1,
      pageSize: 15,
      totalEstimatedNetSellValue: 0,
      totalEstimatedGainLoss: 0,
    })
    const first = deferred<Awaited<ReturnType<typeof api.stocks.lookup>>>()
    const second = deferred<Awaited<ReturnType<typeof api.stocks.lookup>>>()
    vi.spyOn(api.stocks, 'lookup')
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise)

    const wrapper = mountWithAppProviders(StocksPage)
    const createButton = wrapper.findAll('button').find(button => button.text().includes('新增股票'))
    await createButton!.trigger('click')
    await flushPromises()
    const symbolInput = document.querySelector<HTMLInputElement>('input[placeholder="e.g. 2330"]')!
    symbolInput.value = '2330'
    symbolInput.dispatchEvent(new Event('input'))
    await nextTick()
    await vi.advanceTimersByTimeAsync(400)
    const market = document.querySelector<HTMLSelectElement>('select')!
    market.value = 'Tpex'
    market.dispatchEvent(new Event('change'))
    symbolInput.value = '6488'
    symbolInput.dispatchEvent(new Event('input'))
    await nextTick()
    first.resolve({
      name: '台積電',
      currentPrice: 1000,
      market: 'Twse',
      resultCode: 'Completed',
    })
    await flushPromises()

    expect(symbolInput.value).toBe('6488')
    expect(market.value).toBe('Tpex')
    expect(document.querySelector<HTMLInputElement>('input[placeholder="e.g. 台積電"]')!.value).toBe('')
  })

  // 驗證儲存查價期間鎖定關閉操作，且非同步完成後只更新送出時的持股快照。
  it('freezes the edited stock while a save lookup is pending', async () => {
    const stockA = {
      id: 1,
      name: '台積電',
      symbol: '2330',
      market: 'Twse' as const,
      instrumentType: 'Stock' as const,
      shares: 10,
      buyPrice: 500,
      currentPrice: 600,
      broker: 'A 券商',
      lastPriceUpdate: '2026-07-14T00:00:00.000Z',
      grossMarketValue: 6000,
      buyCommission: 0,
      sellCommission: 0,
      securitiesTransactionTax: 0,
      estimatedNetSellValue: 6000,
      estimatedGainLoss: 1000,
    }
    const stockB = {
      ...stockA,
      id: 2,
      name: '世芯-KY',
      symbol: '3661',
      market: 'Tpex' as const,
      shares: 20,
      buyPrice: 700,
      currentPrice: 800,
      broker: 'B 券商',
    }
    vi.spyOn(api.stocks, 'list').mockResolvedValue({
      items: [stockA, stockB],
      total: 2,
      page: 1,
      pageSize: 15,
      totalEstimatedNetSellValue: 22000,
      totalEstimatedGainLoss: 2000,
    })
    const lookup = deferred<Awaited<ReturnType<typeof api.stocks.lookup>>>()
    vi.spyOn(api.stocks, 'lookup').mockReturnValue(lookup.promise)
    const update = vi.spyOn(api.stocks, 'update').mockResolvedValue(stockA)

    const wrapper = mountWithAppProviders(StocksPage)
    await flushPromises()
    const stockRows = wrapper.findAll('tbody tr')
    await stockRows[0].findAll('button')[0].trigger('click')
    await flushPromises()
    const form = document.body.querySelector('form')!
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await flushPromises()

    const cancelButton = Array.from(document.body.querySelectorAll<HTMLButtonElement>('button'))
      .find(button => button.textContent?.includes('取消'))!
    const closeButton = document.body.querySelector<HTMLButtonElement>('button[aria-label="關閉"]')!
    const controlsLocked = cancelButton.disabled && closeButton.disabled
    cancelButton.click()
    await nextTick()
    await stockRows[1].findAll('button')[0].trigger('click')
    await flushPromises()

    lookup.resolve({
      name: '台積電',
      currentPrice: 650,
      market: 'Twse',
      resultCode: 'Completed',
    })
    await flushPromises()

    expect.soft(controlsLocked).toBe(true)
    expect.soft(update).toHaveBeenCalledTimes(1)
    expect.soft(update).toHaveBeenCalledWith(1, {
      name: '台積電',
      symbol: '2330',
      market: 'Twse',
      instrumentType: 'Stock',
      shares: 10,
      buyPrice: 500,
      currentPrice: 650,
      broker: 'A 券商',
      lastPriceUpdate: expect.any(String),
    })
  })

  // 驗證儲存查價期間停用所有表單控制項，且 DOM 事件不會改變已送出的持股快照。
  it('disables all stock form controls while a save lookup is pending', async () => {
    const stock = {
      id: 1,
      name: '台積電',
      symbol: '2330',
      market: 'Twse' as const,
      instrumentType: 'Stock' as const,
      shares: 10,
      buyPrice: 500,
      currentPrice: 600,
      broker: 'A 券商',
      lastPriceUpdate: '2026-07-14T00:00:00.000Z',
      grossMarketValue: 6000,
      buyCommission: 0,
      sellCommission: 0,
      securitiesTransactionTax: 0,
      estimatedNetSellValue: 6000,
      estimatedGainLoss: 1000,
    }
    vi.spyOn(api.stocks, 'list').mockResolvedValue({
      items: [stock],
      total: 1,
      page: 1,
      pageSize: 15,
      totalEstimatedNetSellValue: 6000,
      totalEstimatedGainLoss: 1000,
    })
    const lookup = deferred<Awaited<ReturnType<typeof api.stocks.lookup>>>()
    vi.spyOn(api.stocks, 'lookup').mockReturnValue(lookup.promise)
    const update = vi.spyOn(api.stocks, 'update').mockResolvedValue(stock)

    const wrapper = mountWithAppProviders(StocksPage)
    await flushPromises()
    await wrapper.find('tbody tr').findAll('button')[0].trigger('click')
    await flushPromises()
    const form = document.body.querySelector<HTMLFormElement>('form')!
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await flushPromises()

    const controls = Array.from(form.querySelectorAll<HTMLInputElement | HTMLSelectElement>('input, select'))
    const controlsDisabled = controls.length > 0 && controls.every(control => control.matches(':disabled'))
    const symbolInput = form.querySelector<HTMLInputElement>('input[placeholder="e.g. 2330"]')!
    const nameInput = form.querySelector<HTMLInputElement>('input[placeholder="e.g. 台積電"]')!
    const marketSelect = form.querySelector<HTMLSelectElement>('select')!
    const sharesInput = form.querySelector<HTMLInputElement>('input[type="number"][step="1"]')!
    const syncCheckbox = form.querySelector<HTMLInputElement>('#syncPrice')!
    symbolInput.value = '3661'
    symbolInput.dispatchEvent(new Event('input', { bubbles: true }))
    nameInput.value = '等待期間修改'
    nameInput.dispatchEvent(new Event('input', { bubbles: true }))
    marketSelect.value = 'Tpex'
    marketSelect.dispatchEvent(new Event('change', { bubbles: true }))
    sharesInput.value = '999'
    sharesInput.dispatchEvent(new Event('input', { bubbles: true }))
    syncCheckbox.click()

    lookup.resolve({
      name: '台積電',
      currentPrice: 650,
      market: 'Twse',
      resultCode: 'Completed',
    })
    await flushPromises()

    expect.soft(controlsDisabled).toBe(true)
    expect.soft(update).toHaveBeenCalledTimes(1)
    expect.soft(update).toHaveBeenCalledWith(1, {
      name: '台積電',
      symbol: '2330',
      market: 'Twse',
      instrumentType: 'Stock',
      shares: 10,
      buyPrice: 500,
      currentPrice: 650,
      broker: 'A 券商',
      lastPriceUpdate: expect.any(String),
    })
  })

  // 驗證 mutation 成功後先解除儲存鎖定，清單刷新失敗只顯示專用錯誤且不重送 mutation。
  it('reports a post-save refresh failure without reporting save failure', async () => {
    const stock = {
      id: 1,
      name: '台積電',
      symbol: '2330',
      market: 'Twse' as const,
      instrumentType: 'Stock' as const,
      shares: 10,
      buyPrice: 500,
      currentPrice: 600,
      broker: 'A 券商',
      lastPriceUpdate: '2026-07-14T00:00:00.000Z',
      grossMarketValue: 6000,
      buyCommission: 0,
      sellCommission: 0,
      securitiesTransactionTax: 0,
      estimatedNetSellValue: 6000,
      estimatedGainLoss: 1000,
    }
    const listResponse = {
      items: [stock],
      total: 1,
      page: 1,
      pageSize: 15,
      totalEstimatedNetSellValue: 6000,
      totalEstimatedGainLoss: 1000,
    }
    const refresh = deferred<Awaited<ReturnType<typeof api.stocks.list>>>()
    vi.spyOn(api.stocks, 'list')
      .mockResolvedValueOnce(listResponse)
      .mockReturnValueOnce(refresh.promise)
    vi.spyOn(api.stocks, 'lookup').mockResolvedValue({
      name: '台積電',
      currentPrice: 650,
      market: 'Twse',
      resultCode: 'Completed',
    })
    const update = vi.spyOn(api.stocks, 'update').mockResolvedValue(stock)
    const toast = { success: vi.fn(), error: vi.fn() }

    const wrapper = mount(StocksPage, { global: { provide: { toast } } })
    await flushPromises()
    const editButton = wrapper.find('tbody tr').findAll('button')[0]
    await editButton.trigger('click')
    await flushPromises()
    document.body.querySelector<HTMLFormElement>('form')!
      .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await flushPromises()

    await editButton.trigger('click')
    await flushPromises()
    const closeButton = document.body.querySelector<HTMLButtonElement>('button[aria-label="關閉"]')!
    const unlockedWhileRefreshing = !closeButton.matches(':disabled')
    refresh.reject()
    await flushPromises()

    expect.soft(unlockedWhileRefreshing).toBe(true)
    expect.soft(update).toHaveBeenCalledTimes(1)
    expect.soft(toast.success).toHaveBeenCalledWith('股票已更新')
    expect.soft(toast.error).toHaveBeenCalledWith('股票已儲存，但重新整理清單失敗')
    expect.soft(toast.error).not.toHaveBeenCalledWith('儲存失敗')
  })

  // 驗證新增送出時使進行中的 symbol lookup 失效，避免成功後因表單 identity 改變而無法關閉。
  it('closes the create modal after a pending symbol lookup resolves before create', async () => {
    vi.useFakeTimers()
    const listResponse = {
      items: [],
      total: 0,
      page: 1,
      pageSize: 15,
      totalEstimatedNetSellValue: 0,
      totalEstimatedGainLoss: 0,
    }
    vi.spyOn(api.stocks, 'list').mockResolvedValue(listResponse)
    const lookup = deferred<Awaited<ReturnType<typeof api.stocks.lookup>>>()
    vi.spyOn(api.stocks, 'lookup').mockReturnValue(lookup.promise)
    const createResult = {
      id: 1,
      name: '自訂名稱',
      symbol: '2330',
      market: 'Unknown' as const,
      instrumentType: 'Stock' as const,
      shares: 10,
      buyPrice: 500,
      currentPrice: 0,
      broker: null,
      lastPriceUpdate: null,
    }
    const createRequest = deferred<Awaited<ReturnType<typeof api.stocks.create>>>()
    const create = vi.spyOn(api.stocks, 'create').mockReturnValue(createRequest.promise)

    const wrapper = mountWithAppProviders(StocksPage)
    await flushPromises()
    const createButton = wrapper.findAll('button').find(button => button.text().includes('新增股票'))!
    await createButton.trigger('click')
    await flushPromises()
    const form = document.body.querySelector<HTMLFormElement>('form')!
    const nameInput = form.querySelector<HTMLInputElement>('input[placeholder="e.g. 台積電"]')!
    const symbolInput = form.querySelector<HTMLInputElement>('input[placeholder="e.g. 2330"]')!
    const sharesInput = form.querySelector<HTMLInputElement>('input[type="number"][step="1"]')!
    const priceInputs = form.querySelectorAll<HTMLInputElement>('input[type="number"][step="0.01"]')
    nameInput.value = '自訂名稱'
    nameInput.dispatchEvent(new Event('input', { bubbles: true }))
    sharesInput.value = '10'
    sharesInput.dispatchEvent(new Event('input', { bubbles: true }))
    priceInputs[0].value = '500'
    priceInputs[0].dispatchEvent(new Event('input', { bubbles: true }))
    symbolInput.value = '2330'
    symbolInput.dispatchEvent(new Event('input', { bubbles: true }))
    await nextTick()
    await vi.advanceTimersByTimeAsync(400)
    expect(api.stocks.lookup).toHaveBeenCalledTimes(1)

    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    await flushPromises()
    lookup.resolve({
      name: '台積電',
      currentPrice: 650,
      market: 'Twse',
      resultCode: 'Completed',
    })
    await flushPromises()
    const marketAfterLookup = form.querySelector<HTMLSelectElement>('select')!.value
    const currentPriceAfterLookup = priceInputs[1].value
    createRequest.resolve(createResult)
    await flushPromises()

    expect.soft(create).toHaveBeenCalledTimes(1)
    expect.soft(marketAfterLookup).toBe('Unknown')
    expect.soft(currentPriceAfterLookup).toBe('')
    expect.soft(document.body.querySelector('form')).toBeNull()
  })
})
