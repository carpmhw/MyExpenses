import { mount } from '@vue/test-utils'
import { ref } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../src/api'
import StockPortfolioOverview from '../../src/components/reports/StockPortfolioOverview.vue'
import type { StockMarketRiskReport, StockPerformanceReport, StockStructureReport, StockValueTrendPoint } from '../../src/types'
import { deferred } from '../support/deferred'
import { mountWithAppProviders } from '../support/render'
import { Line } from 'vue-chartjs'

vi.mock('vue-chartjs', () => ({
  Line: { props: ['data', 'options'], template: '<div data-testid="line-chart">{{ JSON.stringify(data.labels) }}</div>' },
}))

// 等待 Vue watcher 與非同步查詢完成狀態更新。
async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

// 建立總覽結構查詢的完整成功資料。
function createStructureReport(): StockStructureReport {
  return {
    summary: {
      holdingCount: 2,
      totalEstimatedBuyCost: 180000,
      totalGrossMarketValue: 210000,
      totalEstimatedNetSellValue: 209500,
      totalEstimatedGainLoss: 29500,
      estimatedGainLossPercentage: 16.39,
    },
    insights: [],
    symbolAllocations: [],
    instrumentTypeAllocations: [
      { key: 'Stock', label: '股票', value: 150000, percentage: 71.6 },
      { key: 'StockEtf', label: 'ETF', value: 59500, percentage: 28.4 },
    ],
    brokerAllocations: [],
    marketAllocations: [{ key: 'Twse', label: '上市', value: 209500, percentage: 100 }],
    concentration: {
      top1Percentage: 71.6,
      top3Percentage: 100,
      top5Percentage: 100,
      hhi: 0.593,
      effectiveHoldingCount: 1.7,
    },
    dataQuality: {
      holdingCount: 2,
      positivePriceCount: 2,
      missingLastPriceUpdateCount: 1,
      stalePriceCount: 1,
      positivePriceCoverage: 1,
      oldestLastPriceUpdateUtc: '2026-08-01T00:00:00Z',
      latestLastPriceUpdateUtc: '2026-08-06T00:00:00Z',
      staleAfterHours: 72,
      generatedAtUtc: '2026-08-06T00:00:00Z',
    },
    holdings: [
      {
        id: 1, name: '台積電', symbol: '2330', instrumentType: 'Stock', shares: 1000,
        buyPrice: 150, currentPrice: 200, broker: '甲券商', grossMarketValue: 200000,
        buyCommission: 0, sellCommission: 0, securitiesTransactionTax: 0, estimatedBuyCost: 150000,
        estimatedNetSellValue: 150000, estimatedGainLoss: 0, allocationPercentage: 71.6,
      },
    ],
    availableBrokers: [],
    availableInstrumentTypes: [],
    generatedAt: '2026-08-06T00:00:00Z',
  }
}

// 建立總覽風險查詢的完整成功資料。
function createRiskReport(overrides: Partial<StockMarketRiskReport> = {}): StockMarketRiskReport {
  return {
    periodMonths: 12,
    scenarioDescription: '目前持股歷史情境',
    calculationDate: '2026-08-07',
    dataCutoffDate: '2026-08-06',
    portfolioAnnualizedVolatility: { value: 0.2, unavailableReason: null },
    portfolioMaximumDrawdown: { value: -0.15, unavailableReason: null },
    eligibleMarketValueCoverage: 0.95,
    eligibleMarketValueCoverageMetric: { value: 0.95, unavailableReason: null },
    coverageThreshold: 0.9,
    commonObservationCount: 200,
    totalHoldingCount: 2,
    includedInstruments: [],
    excludedInstruments: [],
    volatilityRanking: [],
    correlationMatrix: { labels: [], values: [], commonObservationCount: 200, unavailableReason: null },
    syncWarnings: [],
    riskContributions: [
      { name: '次要風險', symbol: 'BBB', market: 'Tpex', grossMarketValue: 50000, weight: 0.25, componentVolatilityContribution: 0.02, contributionPercentage: 0.1 },
      { name: '分散標的', symbol: 'CCC', market: 'Twse', grossMarketValue: 20000, weight: 0.1, componentVolatilityContribution: -0.02, contributionPercentage: -0.1 },
      { name: '主要風險', symbol: 'AAA', market: 'Twse', grossMarketValue: 130000, weight: 0.65, componentVolatilityContribution: 0.18, contributionPercentage: 0.9 },
    ],
    ...overrides,
  }
}

const trend: StockValueTrendPoint[] = [
  { month: '2026/07', snapshotDate: '2026-07-01T00:00:00Z', name: '七月快照', totalStockValue: 200000, basis: 'AssetsOnly' },
  { month: '2026/08', snapshotDate: '2026-08-01T00:00:00Z', name: '八月快照', totalStockValue: 209500, basis: 'AssetsOnly' },
]

function createPerformanceReport(overrides: Partial<StockPerformanceReport> = {}): StockPerformanceReport {
  return {
    dateStart: '2026-01-01',
    dateEnd: '2026-08-07',
    trackingStartDate: '2026-01-01',
    hasSyntheticOpeningBalances: false,
    terminalValuationSource: 'CurrentGrossMarketValue',
    ledgerCoverage: { value: 1, unavailableReason: 'None' },
    summary: { currentGrossMarketValue: 209500, remainingCostBasis: 180000, realizedGainLoss: 1000, unrealizedGainLoss: 28500, netDividendIncome: 500, totalGainLoss: 30000 },
    twr: { value: 0.12, unavailableReason: 'None' },
    xirr: { value: 0.18, unavailableReason: 'None' },
    monthlyPoints: [{ month: '2026-08', endingMarketValue: 209500, netContribution: 0, realizedGainLoss: 1000, dividendIncome: 500, cumulativeTwr: 0.12 }],
    instrumentBreakdown: [{ stockId: 1, name: '台積電', symbol: '2330', market: 'Twse', broker: '甲券商', currentShares: 1000, grossMarketValue: 209500, remainingCostBasis: 180000, realizedGainLoss: 1000, unrealizedGainLoss: 28500, dividendIncome: 500, totalGainLoss: 30000, isClosed: false }],
    dataQuality: { activeInstrumentCount: 1, ledgerManagedInstrumentCount: 1, priceObservationCount: 10, priceCoverage: 1, trackingStartReason: 'None', hasIncompleteLedgerCoverage: false },
    ...overrides,
  }
}

describe('StockPortfolioOverview', () => {
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('renders all six KPIs with allocation, instrument summary, concentration, trend, top-five risk contributions, and quality warning', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    for (const label of ['預估賣出淨值', '預估未實現損益', '預估損益率', 'Top 1 占比', '12M 年化波動度', '12M 最大回撤']) {
      expect(wrapper.text()).toContain(label)
    }
    expect(wrapper.text()).toContain('市場配置')
    expect(wrapper.text()).toContain('商品類型摘要')
    expect(wrapper.text()).toContain('HHI')
    expect(wrapper.text()).toContain('歷史快照資產價值，不等同投資報酬率')
    expect(wrapper.text()).toContain('主要風險')
    expect(wrapper.text()).toContain('分散標的')
    expect(wrapper.text()).toContain('-10.0%')
    expect(wrapper.text()).toContain('資料新鮮度提示')
    expect(wrapper.text()).toContain('最舊更新 2026-08-01 00:00:00 UTC')
    expect(wrapper.text()).toContain('最新更新 2026-08-06 00:00:00 UTC')
  })

  // 驗證總覽顯示績效摘要，並提供前往完整績效 tab 的導覽事件。
  it('renders the performance summary and navigates to the full report', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)
    vi.spyOn(api.reports, 'stockPerformance').mockResolvedValue(createPerformanceReport())

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    expect(wrapper.get('[data-testid="overview-performance-summary"]').text()).toContain('12.00%')
    await wrapper.get('[data-testid="navigate-stock-performance"]').trigger('click')
    expect(wrapper.emitted('navigate')).toContainEqual(['stockPerformance'])
  })

  // 驗證總覽績效 request 使用系統時區今天，而不是完整年度最後一天。
  it('queries the performance summary through the system-local today', async () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-08-28T12:00:00.000Z'))
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)
    const query = vi.spyOn(api.reports, 'stockPerformance').mockResolvedValue(createPerformanceReport())

    mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    expect(query).toHaveBeenCalledWith(
      { dateStart: '2026-01-01', dateEnd: '2026-08-28' },
      expect.anything(),
    )
  })

  // 驗證總覽顯示兩位小數、兩種報酬方法短說明與共同提示。
  it('renders precise return metrics and method guidance', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)
    vi.spyOn(api.reports, 'stockPerformance').mockResolvedValue(createPerformanceReport())

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    const summary = wrapper.get('[data-testid="overview-performance-summary"]')
    expect(summary.text()).toContain('12.00%')
    expect(summary.text()).toContain('18.00%')
    expect(summary.text()).toContain('排除資金進出時點影響')
    expect(summary.text()).toContain('考慮投入金額與時間')
    expect(wrapper.get('[data-testid="overview-return-method-note"]').text()).toBe(
      'TWR 與 XIRR 採用不同計算觀點，數值不同屬正常現象。',
    )
  })

  // 驗證總覽績效不可用時保留 typed reason，且不以零值取代。
  it('preserves unavailable return reasons without formatting zero percentages', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)
    vi.spyOn(api.reports, 'stockPerformance').mockResolvedValue(createPerformanceReport({
      twr: { value: null, unavailableReason: 'NoLedgerHistory' },
      xirr: { value: null, unavailableReason: 'InsufficientCashFlows' },
    }))

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    const summary = wrapper.get('[data-testid="overview-performance-summary"]')
    expect(summary.text()).toContain('不可用：尚無 Ledger 歷史')
    expect(summary.text()).toContain('不可用：現金流不足')
    expect(summary.text()).not.toContain('0.00%')
  })

  // 驗證績效摘要失敗時只影響自己的 QueryState，既有結構與風險仍可用。
  it('keeps structure and risk visible when performance summary fails', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)
    vi.spyOn(api.reports, 'stockPerformance').mockRejectedValue(new Error('performance unavailable'))

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    expect(wrapper.text()).toContain('預估賣出淨值')
    expect(wrapper.text()).toContain('12M 年化波動度')
    expect(wrapper.text()).toContain('performance unavailable')
  })

  it('keeps successful structure sections visible when the independent risk query fails', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockRejectedValue(new Error('risk unavailable'))
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    expect(wrapper.text()).toContain('預估賣出淨值')
    expect(wrapper.text()).toContain('市場配置')
    expect(wrapper.text()).toContain('risk unavailable')
  })

  it('keeps successful risk and trend sections visible when the independent structure query fails', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockRejectedValue(new Error('structure unavailable'))
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    expect(wrapper.text()).toContain('structure unavailable')
    expect(wrapper.text()).toContain('12M 年化波動度')
    expect(wrapper.get('[data-testid="line-chart"]').text()).toContain('2026/07')
  })

  it('keeps successful structure and risk sections visible when the independent trend query fails', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    vi.spyOn(api.reports, 'stockValueTrend').mockRejectedValue(new Error('trend unavailable'))

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    expect(wrapper.text()).toContain('預估賣出淨值')
    expect(wrapper.text()).toContain('12M 年化波動度')
    expect(wrapper.text()).toContain('trend unavailable')
  })

  it('renders independent empty states for structure, risk, and trend queries', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue({
      ...createStructureReport(),
      summary: { ...createStructureReport().summary, holdingCount: 0 },
      holdings: [],
    })
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport({ totalHoldingCount: 0 }))
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue([])

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    expect(wrapper.text()).toContain('尚無目前持股')
    expect(wrapper.text()).toContain('尚無目前持股風險資料')
    expect(wrapper.text()).toContain('尚無全部持股價值歷史')
  })

  it('labels missing price update timestamps as unavailable', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue({
      ...createStructureReport(),
      dataQuality: {
        ...createStructureReport().dataQuality,
        oldestLastPriceUpdateUtc: null,
        latestLastPriceUpdateUtc: null,
      },
    })
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    expect(wrapper.text()).toContain('最舊更新 尚無更新時間')
    expect(wrapper.text()).toContain('最新更新 尚無更新時間')
  })

  it('uses semantic warning colors and a freshness tooltip for stale price data', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    const warning = wrapper.get('[data-testid="overview-data-quality-warning"]')
    expect(warning.classes()).toEqual(expect.arrayContaining([
      'border-color-warning-border',
      'bg-color-warning-bg',
      'text-color-warning-text',
    ]))
    expect(warning.attributes('title')).toContain('72 小時')
    expect(warning.attributes('title')).toContain('非交易日曆')
  })

  it('shows market cutoff and coverage in the data-quality section', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    const quality = wrapper.get('[data-testid="overview-data-quality-warning"]')
    expect(quality.text()).toContain('行情截止日 2026-08-06')
    expect(quality.text()).toContain('行情市值覆蓋率 95.0%')
  })

  it('shows market cutoff and coverage as unavailable when the independent risk query fails', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockRejectedValue(new Error('risk unavailable'))
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    const quality = wrapper.get('[data-testid="overview-data-quality-warning"]')
    expect(quality.text()).toContain('行情截止日 不可用')
    expect(quality.text()).toContain('行情市值覆蓋率 不可用')
    expect(quality.text()).not.toContain('行情市值覆蓋率 0.0%')
  })

  it('uses the coverage metric reason instead of legacy zero in the data-quality section', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport({
      eligibleMarketValueCoverage: 0,
      eligibleMarketValueCoverageMetric: { value: null, unavailableReason: 'NonPositiveGrossValue' },
    }))
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    const quality = wrapper.get('[data-testid="overview-data-quality-warning"]')
    expect(quality.text()).toContain('行情市值覆蓋率 不可用：毛市值不是正值')
    expect(quality.text()).not.toContain('行情市值覆蓋率 0.0%')
  })

  it('shows unavailable risk metrics with their typed reason instead of zero', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport({
      portfolioAnnualizedVolatility: { value: null, unavailableReason: 'CoverageBelowThreshold' },
      portfolioMaximumDrawdown: { value: null, unavailableReason: 'InsufficientCommonDates' },
      riskContributions: [],
    }))
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    expect(wrapper.text()).toContain('不可用：覆蓋不足')
    expect(wrapper.text()).toContain('不可用：共同交易日不足')
    expect(wrapper.text()).toContain('尚無可用風險貢獻')
  })

  it('explains that zero portfolio volatility cannot produce top risk contributions', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport({
      portfolioAnnualizedVolatility: { value: 0, unavailableReason: null },
      riskContributions: [],
    }))
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    expect(wrapper.text()).toContain('組合波動度為 0，無法計算風險貢獻')
    expect(wrapper.text()).not.toContain('尚無可用風險貢獻；資料準備中。')
  })

  it('uses 12 months by default and reloads only the selected trend period', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    const valueTrend = vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()
    expect(valueTrend).toHaveBeenCalledWith({ months: 12 }, expect.anything())

    await wrapper.get('[data-testid="overview-value-trend-period-60"]').trigger('click')
    await flushPromises()
    expect(valueTrend).toHaveBeenLastCalledWith({ months: 60 }, expect.anything())
  })

  it('marks the selected value trend period as pressed', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    expect(wrapper.get('[data-testid="overview-value-trend-period-12"]').attributes('aria-pressed')).toBe('true')
    await wrapper.get('[data-testid="overview-value-trend-period-6"]').trigger('click')
    expect(wrapper.get('[data-testid="overview-value-trend-period-6"]').attributes('aria-pressed')).toBe('true')
  })

  it('uses dark theme chart tokens for overview trend legend and tooltip surface', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mount(StockPortfolioOverview, {
      global: { provide: { darkMode: { isDark: ref(true) } } },
    })
    await flushPromises()

    const options = wrapper.findComponent(Line).props('options') as { plugins: { legend: { labels: { color: string } }; tooltip: { backgroundColor: string; titleColor: string; bodyColor: string } } }
    expect(options.plugins.legend.labels.color).toBe('#B8C0CC')
    expect(options.plugins.tooltip.backgroundColor).toBe('#3B4252')
    expect(options.plugins.tooltip.titleColor).toBe('#ECEFF4')
    expect(options.plugins.tooltip.bodyColor).toBe('#ECEFF4')
  })

  it('keeps the newly selected overview trend when the previous period resolves late', async () => {
    const previousTrend = deferred<StockValueTrendPoint[]>()
    const currentTrend = [{ ...trend[0], month: '最新期間' }, { ...trend[1], month: '最新期間-2' }]
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    const valueTrend = vi.spyOn(api.reports, 'stockValueTrend')
      .mockReturnValueOnce(previousTrend.promise)
      .mockResolvedValueOnce(currentTrend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()
    await wrapper.get('[data-testid="overview-value-trend-period-6"]').trigger('click')
    await flushPromises()

    expect(valueTrend).toHaveBeenLastCalledWith({ months: 6 }, expect.anything())
    expect(wrapper.get('[data-testid="line-chart"]').text()).toContain('最新期間')
    previousTrend.resolve([{ ...trend[0], month: '舊期間' }, { ...trend[1], month: '舊期間-2' }])
    await flushPromises()
    expect(wrapper.get('[data-testid="line-chart"]').text()).not.toContain('舊期間')
  })

  it('sorts contributions before limiting them to the top five and preserves negative values', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport({
      riskContributions: [
        ...createRiskReport().riskContributions,
        { name: '第四名', symbol: 'DDD', market: 'Twse', grossMarketValue: 10000, weight: 0.05, componentVolatilityContribution: 0.01, contributionPercentage: 0.05 },
        { name: '第五名', symbol: 'EEE', market: 'Twse', grossMarketValue: 5000, weight: 0.03, componentVolatilityContribution: 0.005, contributionPercentage: 0.03 },
        { name: '第六名', symbol: 'FFF', market: 'Twse', grossMarketValue: 5000, weight: 0.02, componentVolatilityContribution: -0.02, contributionPercentage: -0.2 },
      ],
    }))
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    expect(wrapper.text()).toContain('第五名')
    expect(wrapper.text()).not.toContain('第六名')
    expect(wrapper.text()).toContain('+90.0%')
    expect(wrapper.text()).toContain('-10.0%')
  })

  it('uses explicit empty messages for unavailable allocation summaries', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue({
      ...createStructureReport(),
      marketAllocations: [],
      instrumentTypeAllocations: [],
    })
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    expect(wrapper.text()).toContain('暫無可顯示的市場配置。')
    expect(wrapper.text()).toContain('暫無可顯示的商品類型摘要。')
  })

  it('emits navigation targets for both detail reports', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(createStructureReport())
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createRiskReport())
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockPortfolioOverview)
    await flushPromises()

    await wrapper.get('[data-testid="navigate-stock-structure"]').trigger('click')
    await wrapper.get('[data-testid="navigate-market-risk"]').trigger('click')
    expect(wrapper.emitted('navigate')).toEqual([['stockStructure'], ['marketRisk']])
  })
})
