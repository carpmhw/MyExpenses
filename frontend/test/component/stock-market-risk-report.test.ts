import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../src/api'
import StockMarketRiskReport from '../../src/components/reports/StockMarketRiskReport.vue'
import type { StockMarketRiskReport as StockMarketRiskReportData } from '../../src/types'
import { deferred } from '../support/deferred'
import { mountWithAppProviders } from '../support/render'

async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

function createReport(overrides: Partial<StockMarketRiskReportData> = {}): StockMarketRiskReportData {
  return {
    periodMonths: 12,
    scenarioDescription: '目前持股歷史情境：以目前毛市值權重套用歷史還原日報酬',
    calculationDate: '2026-08-07',
    dataCutoffDate: '2026-08-06',
    portfolioAnnualizedVolatility: { value: 0.2, unavailableReason: null },
    eligibleMarketValueCoverage: 0.95,
    coverageThreshold: 0.9,
    commonObservationCount: 200,
    totalHoldingCount: 1,
    includedInstruments: [{
      name: '台積電',
      symbol: '2330',
      market: 'Twse',
      grossMarketValue: 100000,
      originalWeight: 1,
      renormalizedWeight: 1,
      observations: 200,
      annualizedVolatility: 0.22,
      exclusionReason: null,
    }],
    excludedInstruments: [],
    volatilityRanking: [{
      name: '台積電',
      symbol: '2330',
      market: 'Twse',
      grossMarketValue: 100000,
      weight: 1,
      annualizedVolatility: 0.22,
      observations: 200,
    }],
    correlationMatrix: {
      labels: [{ name: '台積電', symbol: '2330', market: 'Twse' }],
      values: [[1]],
      commonObservationCount: 200,
      unavailableReason: 'NotEnoughEligibleInstruments',
    },
    syncWarnings: [],
    ...overrides,
  }
}

describe('StockMarketRiskReport', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads only after mounting, defaults to 12 months, and ignores stale period responses', async () => {
    const first = deferred<StockMarketRiskReportData>()
    const query = vi.spyOn(api.reports, 'stockMarketRisk')
      .mockReturnValueOnce(first.promise)
      .mockResolvedValueOnce(createReport({ periodMonths: 3 }))
    const wrapper = mountWithAppProviders(StockMarketRiskReport)
    await flushPromises()

    expect(query).toHaveBeenCalledWith({ periodMonths: 12 }, expect.anything())
    await wrapper.get('[data-testid="period-3"]').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('3 個月')
    expect(wrapper.text()).not.toContain('12 個月結果')

    first.resolve(createReport({ periodMonths: 12, scenarioDescription: '12 個月結果' }))
    await flushPromises()
    expect(wrapper.text()).not.toContain('12 個月結果')
  })

  it('renders complete metrics, scenario boundary, ranking, and correlation labels', async () => {
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createReport())
    const wrapper = mountWithAppProviders(StockMarketRiskReport)
    await flushPromises()

    expect(wrapper.text()).toContain('目前持股歷史情境')
    expect(wrapper.text()).toContain('市值覆蓋率')
    expect(wrapper.text()).toContain('95.0%')
    expect(wrapper.text()).toContain('台積電')
    expect(wrapper.text()).toContain('2330')
    expect(wrapper.text()).toContain('相關性矩陣')
  })

  it('explains coverage and common-date unavailable states without showing zero volatility', async () => {
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createReport({
      eligibleMarketValueCoverage: 0.75,
      portfolioAnnualizedVolatility: { value: null, unavailableReason: 'CoverageBelowThreshold' },
      excludedInstruments: [{
        name: '缺少資料',
        symbol: '00679B',
        market: 'Tpex',
        grossMarketValue: 25000,
        originalWeight: 0.25,
        renormalizedWeight: 0,
        observations: 12,
        annualizedVolatility: null,
        exclusionReason: 'InsufficientHistory',
      }],
      correlationMatrix: {
        labels: [],
        values: [],
        commonObservationCount: 12,
        unavailableReason: 'InsufficientCommonDates',
      },
    }))
    const wrapper = mountWithAppProviders(StockMarketRiskReport)
    await flushPromises()

    expect(wrapper.text()).toContain('覆蓋不足')
    expect(wrapper.text()).toContain('資料不足')
    expect(wrapper.text()).toContain('共同交易日不足')
    expect(wrapper.text()).not.toContain('年化波動度：0.0%')
  })

  it('shows no-holdings, syncing warning, and query failure states truthfully', async () => {
    vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue(createReport({
      totalHoldingCount: 0,
      includedInstruments: [],
      volatilityRanking: [],
      portfolioAnnualizedVolatility: { value: null, unavailableReason: 'NoHoldings' },
      syncWarnings: [{
        symbol: '2330',
        market: 'Twse',
        status: 'ProviderError',
        safeMessage: '保留最後成功資料',
        lastAttemptedAtUtc: '2026-08-07T00:00:00Z',
        lastSucceededAtUtc: '2026-08-06T00:00:00Z',
        latestTradingDate: '2026-08-06',
      }],
    }))
    const wrapper = mountWithAppProviders(StockMarketRiskReport)
    await flushPromises()
    expect(wrapper.text()).toContain('尚無目前持股')

    const failing = mountWithAppProviders(StockMarketRiskReport)
    vi.spyOn(api.reports, 'stockMarketRisk').mockRejectedValue(new Error('query failed'))
    await failing.get('[data-testid="period-3"]').trigger('click')
    await flushPromises()
    expect(failing.text()).toContain('query failed')
  })
})
