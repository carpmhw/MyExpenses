import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../src/api'
import StockStructureReport from '../../src/components/reports/StockStructureReport.vue'
import type { StockStructureReport as StockStructureReportData, StockValueTrendPoint } from '../../src/types'
import { deferred } from '../support/deferred'
import { mountWithAppProviders } from '../support/render'

vi.mock('vue-chartjs', () => ({
  Bar: { template: '<div data-testid="bar-chart" />' },
  Line: { template: '<div data-testid="line-chart" />' },
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
    })
    vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue([])

    const wrapper = mountWithAppProviders(StockStructureReport)
    await flushPromises()

    expect(wrapper.text()).toContain('無法計算持股配置比例')
    expect(wrapper.findAll('[data-testid="doughnut-chart"]')).toHaveLength(0)
    expect(wrapper.findAll('[data-testid="bar-chart"]')).toHaveLength(0)
  })
})
