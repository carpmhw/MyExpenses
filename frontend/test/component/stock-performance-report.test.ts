import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import StockPerformanceReport from '../../src/components/reports/StockPerformanceReport.vue'
import { api } from '../../src/api'
import type { StockPerformanceReport as StockPerformanceReportData } from '../../src/types'
import { addCalendarYears, getDateInputValue } from '../../src/utils/timezone'

vi.mock('vue-chartjs', () => ({
  Line: { props: ['data', 'options'], template: '<div data-testid="performance-line-chart" />' },
}))

function createReport(overrides: Partial<StockPerformanceReportData> = {}): StockPerformanceReportData {
  return {
    dateStart: '2026-01-01',
    dateEnd: '2026-08-25',
    trackingStartDate: '2026-01-01',
    hasSyntheticOpeningBalances: false,
    terminalValuationSource: 'CurrentGrossMarketValue',
    ledgerCoverage: { value: 1, unavailableReason: 'None' },
    summary: {
      currentGrossMarketValue: 6000,
      remainingCostBasis: 5000,
      realizedGainLoss: 100,
      unrealizedGainLoss: 900,
      netDividendIncome: 50,
      totalGainLoss: 1050,
    },
    twr: { value: 0.12, unavailableReason: 'None' },
    xirr: { value: 0.18, unavailableReason: 'None' },
    monthlyPoints: [{
      month: '2026-08',
      endingMarketValue: 6000,
      netContribution: 0,
      realizedGainLoss: 100,
      dividendIncome: 50,
      cumulativeTwr: 0.12,
    }],
    instrumentBreakdown: [{
      stockId: 1,
      name: '台積電',
      symbol: '2330',
      market: 'Twse',
      broker: '甲券商',
      currentShares: 10,
      grossMarketValue: 6000,
      remainingCostBasis: 5000,
      realizedGainLoss: 100,
      unrealizedGainLoss: 900,
      dividendIncome: 50,
      totalGainLoss: 1050,
      isClosed: false,
    }],
    dataQuality: {
      activeInstrumentCount: 1,
      ledgerManagedInstrumentCount: 1,
      priceObservationCount: 10,
      priceCoverage: 1,
      trackingStartReason: 'None',
      hasIncompleteLedgerCoverage: false,
    },
    ...overrides,
  }
}

async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

describe('StockPerformanceReport', () => {
  afterEach(() => vi.restoreAllMocks())

  // 驗證期間 preset 會建立穩定的 dateStart/dateEnd query identity。
  it('queries YTD by default and updates the query for one-year preset', async () => {
    const query = vi.spyOn(api.reports, 'stockPerformance').mockResolvedValue(createReport())
    const wrapper = mount(StockPerformanceReport, {
      global: { stubs: { Line: true } },
    })
    await flushPromises()

    const today = getDateInputValue()
    expect(query).toHaveBeenLastCalledWith({ dateStart: `${today.slice(0, 4)}-01-01`, dateEnd: today }, expect.anything())
    await wrapper.get('[data-testid="performance-period"]').setValue('1y')
    await flushPromises()
    expect(query).toHaveBeenLastCalledWith({ dateStart: addCalendarYears(today, -1), dateEnd: today }, expect.anything())
    await wrapper.get('[data-testid="performance-period"]').setValue('3y')
    await flushPromises()
    expect(query).toHaveBeenLastCalledWith({ dateStart: addCalendarYears(today, -3), dateEnd: today }, expect.anything())
    await wrapper.get('[data-testid="performance-period"]').setValue('5y')
    await flushPromises()
    expect(query).toHaveBeenLastCalledWith({ dateStart: addCalendarYears(today, -5), dateEnd: today }, expect.anything())
    await wrapper.get('[data-testid="performance-period"]').setValue('all')
    await flushPromises()
    expect(query).toHaveBeenLastCalledWith({ dateStart: undefined, dateEnd: today }, expect.anything())
  })

  // 驗證投資績效 KPI 顯示兩位小數、完整方法簡述與共同提示。
  it('renders precise return metrics and complete method descriptions', async () => {
    vi.spyOn(api.reports, 'stockPerformance').mockResolvedValue(createReport())
    const wrapper = mount(StockPerformanceReport, { global: { stubs: { Line: true } } })
    await flushPromises()

    const kpis = wrapper.get('[data-testid="performance-kpis"]')
    expect(kpis.text()).toContain('12.00%')
    expect(kpis.text()).toContain('18.00%')
    expect(kpis.text()).toContain('排除資金進出時點影響，反映投資組合本身表現。')
    expect(kpis.text()).toContain('依實際資金投入與取回日期計算的年化報酬。')
    expect(wrapper.get('[data-testid="performance-return-method-note"]').text()).toBe(
      'TWR 與 XIRR 採用不同計算觀點，數值不同屬正常現象。',
    )
  })

  // 驗證 null metric 顯示不可用與 typed reason，且資料品質警告不被吞掉。
  it('renders partial metric availability and data-quality warnings', async () => {
    vi.spyOn(api.reports, 'stockPerformance').mockResolvedValue(createReport({
      hasSyntheticOpeningBalances: true,
      twr: { value: null, unavailableReason: 'InsufficientHistoricalPrices' },
      dataQuality: {
        ...createReport().dataQuality,
        priceCoverage: 0.4,
        trackingStartReason: 'IncompleteLedgerCoverage',
        hasIncompleteLedgerCoverage: true,
      },
    }))
    const wrapper = mount(StockPerformanceReport, { global: { stubs: { Line: true } } })
    await flushPromises()

    expect(wrapper.text()).toContain('不可用')
    expect(wrapper.text()).toContain('歷史價格不足')
    expect(wrapper.text()).toContain('synthetic opening')
    expect(wrapper.text()).toContain('Ledger 覆蓋不完整')
    expect(wrapper.text()).toContain('40.0%')
    expect(wrapper.text()).toContain('2330')
    expect(wrapper.text()).toContain('已實現')
    expect(wrapper.find('table.min-w-\\[940px\\]').exists()).toBe(true)
  })

  // 驗證 TWR 與 XIRR 同時不可用時仍顯示各自原因，不會退化為零百分比。
  it('keeps unavailable return reasons without formatting zero percentages', async () => {
    vi.spyOn(api.reports, 'stockPerformance').mockResolvedValue(createReport({
      twr: { value: null, unavailableReason: 'NoLedgerHistory' },
      xirr: { value: null, unavailableReason: 'NoConvergence' },
    }))
    const wrapper = mount(StockPerformanceReport, { global: { stubs: { Line: true } } })
    await flushPromises()

    expect(wrapper.text()).toContain('不可用')
    expect(wrapper.text()).toContain('尚無 Ledger 歷史')
    expect(wrapper.text()).toContain('計算未收斂')
    const returnKpis = wrapper.get('[data-testid="performance-kpis"]').findAll('.bg-bg-raised').slice(-2)
    expect(returnKpis.every(kpi => !kpi.text().includes('0.00%'))).toBe(true)
    expect(wrapper.get('[data-testid="performance-return-method-note"]').exists()).toBe(true)
  })

  // 驗證舊 request 即使晚回來，也不能覆蓋較新期間的績效結果。
  it('ignores a late response from an older period', async () => {
    let resolveFirst!: (report: StockPerformanceReportData) => void
    let resolveSecond!: (report: StockPerformanceReportData) => void
    const first = new Promise<StockPerformanceReportData>(resolve => { resolveFirst = resolve })
    const second = new Promise<StockPerformanceReportData>(resolve => { resolveSecond = resolve })
    vi.spyOn(api.reports, 'stockPerformance')
      .mockReturnValueOnce(first)
      .mockReturnValueOnce(second)

    const wrapper = mount(StockPerformanceReport, { global: { stubs: { Line: true } } })
    await flushPromises()
    await wrapper.get('[data-testid="performance-period"]').setValue('1y')
    await flushPromises()
    resolveSecond(createReport({ summary: { ...createReport().summary, totalGainLoss: 2222 } }))
    await flushPromises()
    resolveFirst(createReport({ summary: { ...createReport().summary, totalGainLoss: 1111 } }))
    await flushPromises()

    expect(wrapper.text()).toContain('NT$ 2,222')
    expect(wrapper.text()).not.toContain('NT$ 1,111')
  })

  // 驗證 query loading、error 與 retry 狀態不會把失敗誤呈現成零值。
  it('renders loading, error, and retry states', async () => {
    let reject!: (error: Error) => void
    const pending = new Promise<StockPerformanceReportData>((_, rejectPromise) => { reject = rejectPromise })
    const query = vi.spyOn(api.reports, 'stockPerformance')
      .mockReturnValueOnce(pending)
      .mockResolvedValueOnce(createReport())
    const wrapper = mount(StockPerformanceReport, { global: { stubs: { Line: true } } })
    expect(wrapper.get('[role="status"]').text()).toContain('載入中')
    reject(new Error('績效服務暫時無法使用'))
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('績效服務暫時無法使用')
    await wrapper.get('[role="alert"] button').trigger('click')
    await flushPromises()
    expect(query).toHaveBeenCalledTimes(2)
  })

  // 驗證沒有交易或月度資料時顯示 empty state，而不是渲染空圖表。
  it('renders an empty state when the report has no holdings', async () => {
    vi.spyOn(api.reports, 'stockPerformance').mockResolvedValue(createReport({ monthlyPoints: [], instrumentBreakdown: [] }))
    const wrapper = mount(StockPerformanceReport, { global: { stubs: { Line: true } } })
    await flushPromises()

    expect(wrapper.get('[role="status"]').text()).toContain('目前沒有足夠的股票 Ledger 資料')
  })
})
