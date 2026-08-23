import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../src/api'
import StockStructureReport from '../../src/components/reports/StockStructureReport.vue'
import type { StockStructureReport as StockStructureReportData, StockValueTrendPoint } from '../../src/types'
import { deferred } from '../support/deferred'
import { mountWithAppProviders } from '../support/render'

vi.mock('vue-chartjs', () => ({
  Bar: { template: '<div data-testid="bar-chart" />' },
  Line: { props: ['data'], template: '<div data-testid="line-chart">{{ JSON.stringify(data.labels) }}</div>' },
  Doughnut: { template: '<div data-testid="doughnut-chart" />' },
}))

async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

const report: StockStructureReportData = {
  summary: {
    holdingCount: 1,
    totalEstimatedBuyCost: 9000,
    totalGrossMarketValue: 10000,
    totalEstimatedNetSellValue: 9960,
    totalEstimatedGainLoss: 960,
    estimatedGainLossPercentage: 10.67,
  },
  insights: [{
    code: 'NoReminder',
    severity: 'Info',
    message: '目前沒有提醒',
    affectedName: null,
    observedPercentage: null,
    thresholdPercentage: null,
    affectedCount: null,
    amount: null,
  }],
  symbolAllocations: [{ key: 'AAA', label: 'AAA', value: 9960, percentage: 100 }],
  instrumentTypeAllocations: [{ key: 'Stock', label: '股票', value: 9960, percentage: 100 }],
  brokerAllocations: [{ key: '甲券商', label: '甲券商', value: 9960, percentage: 100 }],
  marketAllocations: [{ key: 'Twse', label: '上市', value: 9960, percentage: 100 }],
  concentration: {
    top1Percentage: 100,
    top3Percentage: 100,
    top5Percentage: 100,
    hhi: 1,
    effectiveHoldingCount: 1,
  },
  dataQuality: {
    holdingCount: 1,
    positivePriceCount: 1,
    missingLastPriceUpdateCount: 0,
    stalePriceCount: 1,
    positivePriceCoverage: 1,
    oldestLastPriceUpdateUtc: '2026-08-01T00:00:00Z',
    latestLastPriceUpdateUtc: '2026-08-01T00:00:00Z',
    staleAfterHours: 72,
    generatedAtUtc: '2026-08-06T00:00:00Z',
  },
  holdings: [{
    id: 1,
    name: '標的一',
    symbol: 'AAA',
    instrumentType: 'Stock',
    shares: 100,
    buyPrice: 90,
    currentPrice: 100,
    broker: '甲券商',
    grossMarketValue: 10000,
    buyCommission: 25,
    sellCommission: 25,
    securitiesTransactionTax: 15,
    estimatedBuyCost: 9025,
    estimatedNetSellValue: 9960,
    estimatedGainLoss: 935,
    allocationPercentage: 100,
  }],
  availableBrokers: ['甲券商', '乙券商'],
  availableInstrumentTypes: ['Stock', 'StockEtf'],
  generatedAt: '2026-08-06T00:00:00Z',
}

const trend: StockValueTrendPoint[] = [{
  month: '2026/08',
  snapshotDate: '2026-08-01T00:00:00Z',
  name: '八月快照',
  totalStockValue: 9960,
  basis: 'AssetsOnly',
}]

describe('StockStructureReport', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads current analysis and value trend only when the component is mounted', async () => {
    const structure = vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(report)
    const valueTrend = vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockStructureReport)
    await flushPromises()

    expect(structure).toHaveBeenCalledTimes(1)
    expect(valueTrend).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('標的一')
  })

  it('reloads only current analysis when filters change and clears the old result', async () => {
    const nextReport = deferred<StockStructureReportData>()
    const structure = vi.spyOn(api.reports, 'stockStructure')
      .mockResolvedValueOnce(report)
      .mockReturnValueOnce(nextReport.promise)
    const valueTrend = vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)
    const wrapper = mountWithAppProviders(StockStructureReport)
    await flushPromises()

    await wrapper.get('[data-testid="broker-filter"]').setValue('乙券商')
    await flushPromises()

    expect(structure).toHaveBeenCalledTimes(2)
    expect(valueTrend).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).not.toContain('標的一')

    nextReport.resolve({ ...report, holdings: [], summary: { ...report.summary, holdingCount: 0 } })
    await flushPromises()
  })

  it('keeps current analysis usable when the value trend query fails', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(report)
    vi.spyOn(api.reports, 'stockValueTrend').mockRejectedValue(new Error('trend unavailable'))

    const wrapper = mountWithAppProviders(StockStructureReport)
    await flushPromises()

    expect(wrapper.text()).toContain('標的一')
    expect(wrapper.text()).toContain('trend unavailable')
  })

  it('shows truthful empty states for no current holdings and one trend point', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue({ ...report, holdings: [], summary: { ...report.summary, holdingCount: 0 } })
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockStructureReport)
    await flushPromises()

    expect(wrapper.text()).toContain('沒有符合篩選的持股')
    expect(wrapper.text()).toContain('目前只有 1 筆全部持股價值快照')
  })

  it('does not render allocation charts when the net sell value denominator is unavailable', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue({
      ...report,
      summary: { ...report.summary, totalEstimatedNetSellValue: 0 },
      symbolAllocations: [],
      instrumentTypeAllocations: [],
      brokerAllocations: [],
      marketAllocations: [],
    })
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue([])

    const wrapper = mountWithAppProviders(StockStructureReport)
    await flushPromises()

    expect(wrapper.text()).toContain('無法計算持股配置比例')
    expect(wrapper.findAll('[data-testid="doughnut-chart"]')).toHaveLength(0)
    expect(wrapper.findAll('[data-testid="bar-chart"]')).toHaveLength(0)
  })

  it('renders market allocation, concentration, and a 72-hour data-quality warning', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(report)
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockStructureReport)
    await flushPromises()

    expect(wrapper.text()).toContain('市場配置')
    expect(wrapper.findAll('[data-testid="doughnut-chart"]')).toHaveLength(3)
    expect(wrapper.text()).toContain('Top 1')
    expect(wrapper.text()).toContain('HHI')
    expect(wrapper.text()).toContain('價格資料品質')
    expect(wrapper.text()).toContain('72 小時')
  })

  it('adds explanatory Top 5 and HHI context without changing fixed concentration reminders', async () => {
    const fixedReminder = '單一標的占比超過固定提醒門檻。'
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue({
      ...report,
      insights: [{
        code: 'SingleHoldingConcentration',
        severity: 'Warning',
        message: fixedReminder,
        affectedName: '標的一',
        observedPercentage: 100,
        thresholdPercentage: 50,
        affectedCount: null,
        amount: null,
      }],
    })
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockStructureReport)
    await flushPromises()

    expect(wrapper.text()).toContain(fixedReminder)
    expect(wrapper.text()).toContain('Top 5 涵蓋目前持股中前五大標的的預估賣出淨值占比')
    expect(wrapper.text()).toContain('HHI 越接近 1，代表目前配置越集中')
    expect(wrapper.get('[data-testid="concentration-insights"]').text()).not.toContain('健康評等')
    expect(wrapper.get('[data-testid="concentration-insights"]').text()).not.toContain('買賣建議')
  })

  it('uses semantic warning colors and a freshness tooltip for data-quality limitations', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(report)
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)

    const wrapper = mountWithAppProviders(StockStructureReport)
    await flushPromises()

    const warning = wrapper.get('[data-testid="data-quality-warning"]')
    expect(warning.classes()).toEqual(expect.arrayContaining([
      'border-color-warning-border',
      'bg-color-warning-bg',
      'text-color-warning-text',
    ]))
    expect(warning.attributes('title')).toContain('72 小時')
  })

  it('defaults the all-holdings trend to 12 months and reloads for each allowed period', async () => {
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(report)
    const valueTrend = vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue(trend)
    const wrapper = mountWithAppProviders(StockStructureReport)
    await flushPromises()

    expect(valueTrend).toHaveBeenCalledWith({ months: 12 }, expect.anything())

    for (const months of [6, 24, 36, 60]) {
      await wrapper.get(`[data-testid="value-trend-period-${months}"]`).trigger('click')
      await flushPromises()
      expect(valueTrend).toHaveBeenLastCalledWith({ months }, expect.anything())
    }

    expect(wrapper.text()).toContain('不受目前篩選影響')
    expect(wrapper.text()).toContain('歷史快照資產價值，不等同投資報酬率')
  })

  it('keeps the newly selected trend when the previous period resolves late', async () => {
    const previousTrend = deferred<StockValueTrendPoint[]>()
    const currentTrend: StockValueTrendPoint[] = [
      { ...trend[0], month: '2026/07' },
      { ...trend[0], month: '2026/08' },
    ]
    vi.spyOn(api.reports, 'stockStructure').mockResolvedValue(report)
    const valueTrend = vi.spyOn(api.reports, 'stockValueTrend')
      .mockReturnValueOnce(previousTrend.promise)
      .mockResolvedValueOnce(currentTrend)
    const wrapper = mountWithAppProviders(StockStructureReport)
    await flushPromises()

    await wrapper.get('[data-testid="value-trend-period-6"]').trigger('click')
    await flushPromises()
    expect(valueTrend).toHaveBeenLastCalledWith({ months: 6 }, expect.anything())
    expect(wrapper.get('[data-testid="line-chart"]').text()).toContain('2026/07')

    previousTrend.resolve([{ ...trend[0], month: '舊期間' }, { ...trend[0], month: '舊期間-2' }])
    await flushPromises()

    expect(wrapper.get('[data-testid="line-chart"]').text()).toContain('2026/07')
    expect(wrapper.get('[data-testid="line-chart"]').text()).not.toContain('舊期間')
  })
})
