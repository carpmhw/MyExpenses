import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../src/api'
import StocksPage from '../../src/pages/stocks/index.vue'
import { mountWithAppProviders } from '../support/render'

async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

describe('StocksPage market contract', () => {
  afterEach(() => {
    vi.restoreAllMocks()
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
})
