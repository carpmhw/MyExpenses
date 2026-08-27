import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import StockTransactionModal from '../../src/components/stocks/StockTransactionModal.vue'
import { ApiError, api } from '../../src/api'
import type { StockListItem, StockTransactionListItem } from '../../src/types'
import { deferred } from '../support/deferred'

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

const existingBuyTransaction: StockTransactionListItem = {
  id: 9,
  stockId: 1,
  stockName: '台積電',
  symbol: '2330',
  market: 'Twse',
  broker: '甲券商',
  type: 'Buy',
  tradeDate: '2026-02-01',
  sequence: 1,
  shares: 5,
  price: 550,
  fee: 18,
  tax: 149,
  cashAmount: null,
  openingMarketValue: null,
  notes: '歷史交易',
  grossAmount: 2750,
  netCashFlow: -2917,
  allocatedCostBasis: null,
  realizedGainLoss: 0,
  netDividend: 0,
  remainingShares: 15,
  remainingCostBasis: 2900,
  executionAveragePrice: 550,
}

type ModalTestProps = {
  open: boolean
  stocks: StockListItem[]
  stockId: number | null
  transaction: StockTransactionListItem | null
  initialType: 'Buy' | 'Sell' | 'Dividend'
  stockOptionsStatus: 'idle' | 'loading' | 'ready' | 'error'
  loading: boolean
  errorMessage: string
}

const stockDividendType: ModalTestProps['initialType'] = 'StockDividend'

// 建立交易 Modal 測試 wrapper，集中套用既有 Modal stub 與新增交易預設值。
function mountTransactionModal(overrides: Partial<ModalTestProps> = {}) {
  return mount(StockTransactionModal, {
    props: {
      open: true,
      stocks,
      stockId: 1,
      transaction: null,
      loading: false,
      ...overrides,
    },
    global: {
      stubs: {
        Modal: { template: '<div v-if="open"><slot /></div>', props: ['open', 'title', 'closeDisabled'] },
      },
    },
  })
}

// 等待 Vue watcher 與微任務完成，讓 debounce 測試只需控制時間軸。
async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

afterEach(() => {
  vi.useRealTimers()
})

describe('StockTransactionModal', () => {
  // 驗證 fresh options 的目前持股優先於編輯交易 replay 後的歷史股數。
  it('uses fresh option shares instead of historical remaining shares', () => {
    const wrapper = mount(StockTransactionModal, {
      props: {
        open: true,
        stocks: [{ ...stocks[0], shares: 600 }],
        stockId: 1,
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
          remainingShares: 1000,
          remainingCostBasis: 0,
          executionAveragePrice: 0,
        },
        stockOptionsStatus: 'ready',
        loading: false,
      },
      global: {
        stubs: {
          Modal: { template: '<div v-if="open"><slot /></div>', props: ['open', 'title', 'closeDisabled'] },
        },
      },
    })

    expect(wrapper.get('[data-testid="transaction-stock-summary"]').text()).toContain('目前持有 600 股')
    expect(wrapper.get('[data-testid="transaction-stock-summary"]').text()).not.toContain('目前持有 1000 股')
    expect(wrapper.text()).toContain('此交易完成後持股：1000 股')
  })

  // 驗證 ready option 回傳的零股數是已確認的目前持股，而不是未知狀態。
  it('renders confirmed zero current shares', () => {
    const wrapper = mount(StockTransactionModal, {
      props: { open: true, stocks: [{ ...stocks[0], shares: 0 }], stockId: 1, transaction: null, stockOptionsStatus: 'ready', loading: false },
      global: {
        stubs: {
          Modal: { template: '<div v-if="open"><slot /></div>', props: ['open', 'title', 'closeDisabled'] },
        },
      },
    })

    expect(wrapper.get('[data-testid="transaction-stock-summary"]').text()).toContain('目前持有 0 股')
    expect(wrapper.text()).not.toContain('載入中')
  })

  // 驗證 options 初次載入時不把目前持股列表的暫時資料當成 authoritative shares。
  it('does not render stale shares while options are loading', () => {
    const wrapper = mount(StockTransactionModal, {
      props: { open: true, stocks: [{ ...stocks[0], shares: 1000 }], stockId: 1, transaction: null, stockOptionsStatus: 'loading', loading: false },
      global: {
        stubs: {
          Modal: { template: '<div v-if="open"><slot /></div>', props: ['open', 'title', 'closeDisabled'] },
        },
      },
    })

    expect(wrapper.text()).toContain('目前持股載入中')
    expect(wrapper.text()).not.toContain('目前持有 1000 股')
  })

  // 驗證 options refresh 期間保留 selector identity，但不保留舊股數的目前持股語意。
  it('does not render retained option shares while refreshing', () => {
    const wrapper = mount(StockTransactionModal, {
      props: { open: true, stocks: [{ ...stocks[0], shares: 1000 }], stockId: 1, transaction: null, stockOptionsStatus: 'loading', loading: false },
      global: {
        stubs: {
          Modal: { template: '<div v-if="open"><slot /></div>', props: ['open', 'title', 'closeDisabled'] },
        },
      },
    })

    expect(wrapper.get('[data-testid="transaction-stock-summary"]').text()).toContain('2330 台積電｜甲券商')
    expect(wrapper.text()).not.toContain('目前持有 1000 股')
  })

  // 驗證 options 失敗時顯示未知狀態，不靜默退回 stale 或歷史股數。
  it('does not render current shares when options fail', () => {
    const wrapper = mount(StockTransactionModal, {
      props: { open: true, stocks: [{ ...stocks[0], shares: 1000 }], stockId: 1, transaction: null, stockOptionsStatus: 'error', loading: false },
      global: {
        stubs: {
          Modal: { template: '<div v-if="open"><slot /></div>', props: ['open', 'title', 'closeDisabled'] },
        },
      },
    })

    expect(wrapper.text()).toContain('目前持股暫時無法取得')
    expect(wrapper.text()).not.toContain('目前持有 1000 股')
  })

  // 驗證 options 缺少歷史標的時仍保留 selector identity，且 remaining shares 使用歷史文案。
  it('keeps historical identity separate from current holdings', () => {
    const wrapper = mount(StockTransactionModal, {
      props: {
        open: true,
        stocks: [],
        stockId: 1,
        stockOptionsStatus: 'ready',
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
          remainingShares: 1000,
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

    expect(wrapper.get('[data-testid="transaction-stock-summary"]').text()).toContain('2330 台積電｜甲券商')
    expect(wrapper.text()).toContain('此交易完成後持股：1000 股')
    expect(wrapper.text()).not.toContain('目前持有 1000 股')
  })

  // 驗證新的 Buy 與 Sell 預設自動估算且費稅尚未確認。
  it('defaults new buy and sell transactions to auto cost mode', () => {
    const buyWrapper = mountTransactionModal({ initialType: 'Buy' })
    const sellWrapper = mountTransactionModal({ initialType: 'Sell' })

    expect(buyWrapper.get('[data-testid="transaction-cost-auto"]').attributes('aria-pressed')).toBe('true')
    expect(sellWrapper.get('[data-testid="transaction-cost-auto"]').attributes('aria-pressed')).toBe('true')
    expect((buyWrapper.get('[data-testid="transaction-fee"]').element as HTMLInputElement).value).toBe('')
    expect((buyWrapper.get('[data-testid="transaction-tax"]').element as HTMLInputElement).value).toBe('')
    expect((buyWrapper.get('[data-testid="transaction-fee"]').element as HTMLInputElement).readOnly).toBe(true)
    buyWrapper.unmount()
    sellWrapper.unmount()
  })

  // 驗證目前 inputs 的成功估算會顯示提示並成為交易 request 的費稅來源。
  it('uses a ready estimate in the transaction request', async () => {
    vi.useFakeTimers()
    const estimate = vi.spyOn(api.stocks.ledger, 'estimateCosts').mockResolvedValue({
      grossAmount: 5000,
      fee: 20,
      tax: 0,
    })
    const wrapper = mountTransactionModal()

    await wrapper.get('[data-testid="transaction-shares"]').setValue('10')
    await wrapper.get('[data-testid="transaction-price"]').setValue('500')
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()

    expect(estimate).toHaveBeenCalledWith(
      { stockId: 1, type: 'Buy', shares: 10, price: 500 },
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    )
    expect((wrapper.get('[data-testid="transaction-fee"]').element as HTMLInputElement).value).toBe('20')
    expect((wrapper.get('[data-testid="transaction-tax"]').element as HTMLInputElement).value).toBe('0')
    expect(wrapper.text()).toContain('系統估算值，實際金額以券商成交明細為準')

    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')
    expect(wrapper.emitted('save')?.[0]?.[0]).toEqual(expect.objectContaining({ fee: 20, tax: 0 }))
  })

  // 驗證 auto Buy 的 confirmed zero tax 可以顯示並合法送出。
  it('allows a ready buy estimate with zero tax', async () => {
    vi.useFakeTimers()
    vi.spyOn(api.stocks.ledger, 'estimateCosts').mockResolvedValue({
      grossAmount: 1000,
      fee: 20,
      tax: 0,
    })
    const wrapper = mountTransactionModal()

    await wrapper.get('[data-testid="transaction-shares"]').setValue('1')
    await wrapper.get('[data-testid="transaction-price"]').setValue('1000')
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()
    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')

    expect((wrapper.get('[data-testid="transaction-tax"]').element as HTMLInputElement).value).toBe('0')
    expect(wrapper.emitted('save')?.[0]?.[0]).toEqual(expect.objectContaining({ fee: 20, tax: 0 }))
  })

  // 驗證 estimate loading 期間不顯示假零值且禁止提交。
  it('blocks submission while an estimate is loading', async () => {
    vi.useFakeTimers()
    const pending = deferred<Awaited<ReturnType<typeof api.stocks.ledger.estimateCosts>>>()
    vi.spyOn(api.stocks.ledger, 'estimateCosts').mockReturnValue(pending.promise)
    const wrapper = mountTransactionModal()

    await wrapper.get('[data-testid="transaction-shares"]').setValue('10')
    await wrapper.get('[data-testid="transaction-price"]').setValue('500')
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()
    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')

    expect(wrapper.text()).toContain('正在估算')
    expect((wrapper.get('[data-testid="transaction-fee"]').element as HTMLInputElement).value).toBe('')
    expect(wrapper.emitted('save')).toBeUndefined()
  })

  // 驗證一般 estimate 失敗會阻止 auto submit，並提供切換 manual 的途徑。
  it('blocks submission when an estimate fails', async () => {
    vi.useFakeTimers()
    vi.spyOn(api.stocks.ledger, 'estimateCosts').mockRejectedValue(new ApiError({
      status: 500,
      userMessage: '估算失敗，請稍後再試',
    }))
    const wrapper = mountTransactionModal()

    await wrapper.get('[data-testid="transaction-shares"]').setValue('10')
    await wrapper.get('[data-testid="transaction-price"]').setValue('500')
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()
    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')

    expect(wrapper.text()).toContain('估算失敗，請稍後再試')
    expect(wrapper.text()).toContain('請改用手動輸入')
    expect(wrapper.emitted('save')).toBeUndefined()
  })

  // 驗證 backend unsupported 會阻止 auto submit 並顯示手動恢復路徑。
  it('blocks submission when estimation is unsupported', async () => {
    vi.useFakeTimers()
    vi.spyOn(api.stocks.ledger, 'estimateCosts').mockRejectedValue(new ApiError({
      status: 422,
      code: 'TransactionCostEstimationUnsupported',
      userMessage: '此股票交易不支援自動費稅估算',
    }))
    const wrapper = mountTransactionModal()

    await wrapper.get('[data-testid="transaction-shares"]').setValue('10')
    await wrapper.get('[data-testid="transaction-price"]').setValue('500')
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()
    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')

    expect(wrapper.text()).toContain('此標的無法自動估算')
    expect(wrapper.text()).toContain('請改用手動輸入')
    expect(wrapper.emitted('save')).toBeUndefined()
  })

  // 驗證 manual 欄位空白或負值會阻止提交，明確輸入零則可提交。
  it('validates manual fee and tax without falsy zero fallback', async () => {
    const wrapper = mountTransactionModal()
    await wrapper.get('[data-testid="transaction-cost-manual"]').trigger('click')
    await wrapper.get('[data-testid="transaction-shares"]').setValue('10')
    await wrapper.get('[data-testid="transaction-price"]').setValue('500')
    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')
    expect(wrapper.emitted('save')).toBeUndefined()
    expect(wrapper.text()).toContain('請輸入手續費')
    expect(wrapper.text()).toContain('請輸入交易稅')

    await wrapper.get('[data-testid="transaction-fee"]').setValue('-1')
    await wrapper.get('[data-testid="transaction-tax"]').setValue('0')
    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')
    expect(wrapper.emitted('save')).toBeUndefined()
    expect(wrapper.text()).toContain('手續費不可為負數')

    await wrapper.get('[data-testid="transaction-fee"]').setValue('0')
    await wrapper.get('[data-testid="transaction-tax"]').setValue('0')
    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')
    expect(wrapper.emitted('save')?.[0]?.[0]).toEqual(expect.objectContaining({ fee: 0, tax: 0 }))
  })

  // 驗證新 Dividend 預設 manual 空白欄位且不觸發買賣費稅估算。
  it('keeps new dividends manual and does not estimate costs', async () => {
    const estimate = vi.spyOn(api.stocks.ledger, 'estimateCosts')
    const wrapper = mountTransactionModal({ initialType: 'Dividend' })

    expect(wrapper.get('[data-testid="transaction-cost-manual"]').attributes('aria-pressed')).toBe('true')
    expect((wrapper.get('[data-testid="transaction-fee"]').element as HTMLInputElement).value).toBe('')
    expect((wrapper.get('[data-testid="transaction-tax"]').element as HTMLInputElement).value).toBe('')
    await wrapper.get('[data-testid="transaction-cash-amount"]').setValue('100')
    expect(estimate).not.toHaveBeenCalled()
  })

  // 驗證交易選單區分現金股利與股票股利，且股票股利只顯示股數欄位。
  it('renders stock dividend as a share-only no-cost mode', () => {
    const wrapper = mountTransactionModal({ initialType: stockDividendType })

    expect(wrapper.get('[data-testid="transaction-type"]').text()).toContain('現金股利')
    expect(wrapper.get('[data-testid="transaction-type"]').text()).toContain('股票股利')
    expect(wrapper.find('[data-testid="transaction-shares"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="transaction-price"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="transaction-cash-amount"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="transaction-cost-mode-controls"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="transaction-fee"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="transaction-tax"]').exists()).toBe(false)
  })

  // 驗證尚未初始化 Ledger 的 legacy 持股不可選擇股票股利。
  it('disables stock dividend for an uninitialized stock', () => {
    const wrapper = mountTransactionModal({
      stocks: [{ ...stocks[0], hasLedger: false }],
      initialType: 'Buy',
    })

    expect(wrapper.get('option[value="StockDividend"]').attributes('disabled')).toBeDefined()
  })

  // 驗證切換交易型別後 request 只保留股票股利允許欄位，不殘留價格或現金股利。
  it('normalizes a stock dividend request without stale buy or cash fields', async () => {
    const wrapper = mountTransactionModal({ initialType: 'Buy' })

    await wrapper.get('[data-testid="transaction-cost-manual"]').trigger('click')
    await wrapper.get('[data-testid="transaction-shares"]').setValue('10')
    await wrapper.get('[data-testid="transaction-price"]').setValue('500')
    await wrapper.get('[data-testid="transaction-fee"]').setValue('2')
    await wrapper.get('[data-testid="transaction-tax"]').setValue('1')
    await wrapper.get('[data-testid="transaction-type"]').setValue('Dividend')
    await wrapper.get('[data-testid="transaction-cash-amount"]').setValue('100')
    await wrapper.get('[data-testid="transaction-fee"]').setValue('0')
    await wrapper.get('[data-testid="transaction-tax"]').setValue('0')
    await wrapper.get('[data-testid="transaction-type"]').setValue('StockDividend')
    await wrapper.get('[data-testid="transaction-shares"]').setValue('20')
    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')

    expect(wrapper.emitted('save')?.[0]?.[0]).toEqual(expect.objectContaining({
      type: 'StockDividend',
      shares: 20,
      price: null,
      cashAmount: null,
      fee: 0,
      tax: 0,
    }))
  })

  // 驗證切換至股票股利會取消既有估算，晚到的 Buy response 不得重新寫入隱藏費稅。
  it('cancels a pending estimate when switching to a stock dividend', async () => {
    vi.useFakeTimers()
    const pending = deferred<Awaited<ReturnType<typeof api.stocks.ledger.estimateCosts>>>()
    const estimate = vi.spyOn(api.stocks.ledger, 'estimateCosts').mockReturnValue(pending.promise)
    const wrapper = mountTransactionModal({ initialType: 'Buy' })

    await wrapper.get('[data-testid="transaction-shares"]').setValue('10')
    await wrapper.get('[data-testid="transaction-price"]').setValue('500')
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()
    expect(estimate).toHaveBeenCalledTimes(1)

    await wrapper.get('[data-testid="transaction-type"]').setValue('StockDividend')
    await flushPromises()
    pending.resolve({ grossAmount: 5000, fee: 20, tax: 3 })
    await flushPromises()

    expect(estimate).toHaveBeenCalledTimes(1)
    expect(wrapper.find('[data-testid="transaction-cost-mode-controls"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="transaction-fee"]').exists()).toBe(false)
  })

  // 驗證既有股票股利可編輯日期、股數與備註，且不重新引入買賣或費稅欄位。
  it('edits an existing stock dividend with only date, shares, and notes', async () => {
    const transaction = {
      ...existingBuyTransaction,
      type: stockDividendType as StockTransactionListItem['type'],
      tradeDate: '2026-03-01',
      shares: 100,
      price: null,
      fee: 0,
      tax: 0,
      cashAmount: null,
      openingMarketValue: null,
      notes: '原配股',
    }
    const wrapper = mountTransactionModal({ transaction })

    expect((wrapper.get('[data-testid="transaction-trade-date"]').element as HTMLInputElement).value).toBe('2026-03-01')
    expect((wrapper.get('[data-testid="transaction-shares"]').element as HTMLInputElement).value).toBe('100')
    expect((wrapper.get('[data-testid="transaction-notes"]').element as HTMLInputElement).value).toBe('原配股')
    expect(wrapper.find('[data-testid="transaction-price"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="transaction-cash-amount"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="transaction-cost-mode-controls"]').exists()).toBe(false)
  })

  // 驗證編輯既有交易一律 manual 並保留已保存的 Fee／Tax，不在開啟時自動重算。
  it('preserves saved costs while editing a transaction', async () => {
    const estimate = vi.spyOn(api.stocks.ledger, 'estimateCosts')
    const wrapper = mountTransactionModal({ transaction: existingBuyTransaction })

    expect(wrapper.get('[data-testid="transaction-cost-manual"]').attributes('aria-pressed')).toBe('true')
    expect((wrapper.get('[data-testid="transaction-fee"]').element as HTMLInputElement).value).toBe('18')
    expect((wrapper.get('[data-testid="transaction-tax"]').element as HTMLInputElement).value).toBe('149')
    expect(estimate).not.toHaveBeenCalled()
  })

  // 驗證 ready auto 切換 manual 時以 estimate 作為可覆寫的初始值。
  it('copies a ready estimate into manual fields', async () => {
    vi.useFakeTimers()
    vi.spyOn(api.stocks.ledger, 'estimateCosts').mockResolvedValue({ grossAmount: 5000, fee: 21, tax: 3 })
    const wrapper = mountTransactionModal()
    await wrapper.get('[data-testid="transaction-shares"]').setValue('10')
    await wrapper.get('[data-testid="transaction-price"]').setValue('500')
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()

    await wrapper.get('[data-testid="transaction-cost-manual"]').trigger('click')
    expect((wrapper.get('[data-testid="transaction-fee"]').element as HTMLInputElement).value).toBe('21')
    expect((wrapper.get('[data-testid="transaction-tax"]').element as HTMLInputElement).value).toBe('3')
  })

  // 驗證 manual 切回 auto 時丟棄手動值並重新估算目前輸入。
  it('re-estimates after switching from manual to auto', async () => {
    vi.useFakeTimers()
    const estimate = vi.spyOn(api.stocks.ledger, 'estimateCosts').mockResolvedValue({ grossAmount: 5000, fee: 20, tax: 0 })
    const wrapper = mountTransactionModal()
    await wrapper.get('[data-testid="transaction-cost-manual"]').trigger('click')
    await wrapper.get('[data-testid="transaction-shares"]').setValue('10')
    await wrapper.get('[data-testid="transaction-price"]').setValue('500')
    await wrapper.get('[data-testid="transaction-fee"]').setValue('99')
    await wrapper.get('[data-testid="transaction-tax"]').setValue('88')
    await wrapper.get('[data-testid="transaction-cost-auto"]').trigger('click')
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()

    expect(estimate).toHaveBeenCalledWith(
      { stockId: 1, type: 'Buy', shares: 10, price: 500 },
      expect.objectContaining({ signal: expect.any(AbortSignal) }),
    )
    expect((wrapper.get('[data-testid="transaction-fee"]').element as HTMLInputElement).value).toBe('20')
    expect((wrapper.get('[data-testid="transaction-tax"]').element as HTMLInputElement).value).toBe('0')
  })

  // 驗證較舊的 estimate response 不會覆蓋較新表單輸入的費稅。
  it('ignores an older estimate response after inputs change', async () => {
    vi.useFakeTimers()
    const first = deferred<Awaited<ReturnType<typeof api.stocks.ledger.estimateCosts>>>()
    const second = deferred<Awaited<ReturnType<typeof api.stocks.ledger.estimateCosts>>>()
    vi.spyOn(api.stocks.ledger, 'estimateCosts')
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise)
    const wrapper = mountTransactionModal()

    await wrapper.get('[data-testid="transaction-shares"]').setValue('10')
    await wrapper.get('[data-testid="transaction-price"]').setValue('500')
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()
    await wrapper.get('[data-testid="transaction-shares"]').setValue('20')
    await vi.advanceTimersByTimeAsync(300)
    await flushPromises()

    second.resolve({ grossAmount: 10000, fee: 30, tax: 4 })
    await flushPromises()
    first.resolve({ grossAmount: 5000, fee: 20, tax: 2 })
    await flushPromises()

    expect((wrapper.get('[data-testid="transaction-fee"]').element as HTMLInputElement).value).toBe('30')
    expect((wrapper.get('[data-testid="transaction-tax"]').element as HTMLInputElement).value).toBe('4')
    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')
    expect(wrapper.emitted('save')?.[0]?.[0]).toEqual(expect.objectContaining({ shares: 20, fee: 30, tax: 4 }))
  })

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
    await wrapper.get('[data-testid="transaction-cost-manual"]').trigger('click')
    await wrapper.get('[data-testid="transaction-fee"]').setValue('2')
    await wrapper.get('[data-testid="transaction-tax"]').setValue('0')
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
    await wrapper.get('[data-testid="transaction-fee"]').setValue('0')
    await wrapper.get('[data-testid="transaction-tax"]').setValue('0')
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
    await wrapper.get('[data-testid="transaction-cost-manual"]').trigger('click')
    await wrapper.get('[data-testid="transaction-fee"]').setValue('0')
    await wrapper.get('[data-testid="transaction-tax"]').setValue('0')
    await wrapper.get('[data-testid="stock-transaction-form"]').trigger('submit')
    expect(wrapper.emitted('save')?.[0]?.[0]).toEqual(expect.objectContaining({ stockId: 2 }))
  })
})
