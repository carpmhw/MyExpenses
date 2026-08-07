import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../src/api'
import ReportsPage from '../../src/pages/reports/index.vue'
import SnapshotsPage from '../../src/pages/snapshots/index.vue'
import ComparePage from '../../src/pages/snapshots/compare.vue'
import type { SnapshotBatch } from '../../src/types'
import ConfirmDialog from '../../src/components/ui/ConfirmDialog.vue'
import { createTestRouter, mountWithAppProviders } from '../support/render'

vi.mock('vue-chartjs', () => ({
  Bar: { template: '<div />' },
  Line: { template: '<div />' },
  Doughnut: { template: '<div />' },
}))

// Flushes the promise queues used by Vue watchers and async query execution.
async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

const snapshot: SnapshotBatch = {
  id: 1,
  name: '八月快照',
  snapshotDate: '2026-08-01',
  notes: null,
  totalAssets: 1000,
  totalLiabilities: 100,
  totalNetWorth: 900,
  netWorthBasis: 'AssetsMinusLiabilities',
  totalBankBalance: 800,
  totalStockValue: 200,
  totalStockCost: 180,
  bankDetails: [],
  stockDetails: [],
}

const reportDefaults = {
  trend: [],
  category: [],
  netWorth: { totalAssets: 0, totalLiabilities: 0, netWorth: 0, bankAccounts: [], stocks: [] },
  forecast: [],
}

describe('report and snapshot query ownership', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads only the active report tab and switches query identity with the tab', async () => {
    const trend = vi.spyOn(api.reports, 'incomeExpenseTrend').mockResolvedValue(reportDefaults.trend)
    const category = vi.spyOn(api.reports, 'categoryDistribution').mockResolvedValue(reportDefaults.category)
    const netWorth = vi.spyOn(api.reports, 'netWorth').mockResolvedValue(reportDefaults.netWorth)
    const netWorthTrend = vi.spyOn(api.reports, 'netWorthTrend').mockResolvedValue([])
    const forecast = vi.spyOn(api.reports, 'installmentForecast').mockResolvedValue(reportDefaults.forecast)
    const wrapper = mountWithAppProviders(ReportsPage, {
      global: { stubs: { Bar: { template: '<div />' }, Line: { template: '<div />' }, Doughnut: { template: '<div />' } } },
    })
    await flushPromises()

    expect(trend).toHaveBeenCalledTimes(1)
    expect(category).not.toHaveBeenCalled()
    expect(netWorth).not.toHaveBeenCalled()
    expect(netWorthTrend).not.toHaveBeenCalled()
    expect(forecast).not.toHaveBeenCalled()

    await wrapper.findAll('button').find(button => button.text() === '資產負債')!.trigger('click')
    await flushPromises()

    expect(netWorth).toHaveBeenCalledTimes(1)
    expect(netWorthTrend).toHaveBeenCalledTimes(1)
    expect(forecast).not.toHaveBeenCalled()
  })

  it('lazy-loads stock structure and hides transaction date controls on its tab', async () => {
    const stockStructure = vi.spyOn(api.reports, 'stockStructure').mockResolvedValue({
      summary: { holdingCount: 0, totalEstimatedBuyCost: 0, totalGrossMarketValue: 0, totalEstimatedNetSellValue: 0, totalEstimatedGainLoss: 0, estimatedGainLossPercentage: null },
      insights: [],
      symbolAllocations: [],
      instrumentTypeAllocations: [],
      brokerAllocations: [],
      holdings: [],
      availableBrokers: [],
      availableInstrumentTypes: [],
      generatedAt: '2026-08-06T00:00:00Z',
    })
    const stockValueTrend = vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue([])
    const wrapper = mountWithAppProviders(ReportsPage, {
      global: { stubs: { Bar: { template: '<div />' }, Line: { template: '<div />' }, Doughnut: { template: '<div />' } } },
    })
    await flushPromises()

    expect(stockStructure).not.toHaveBeenCalled()
    expect(stockValueTrend).not.toHaveBeenCalled()

    await wrapper.findAll('button').find(button => button.text() === '持股結構')!.trigger('click')
    await flushPromises()

    expect(stockStructure).toHaveBeenCalledTimes(1)
    expect(stockValueTrend).toHaveBeenCalledTimes(1)
    expect(wrapper.findAll('input[type="date"]')).toHaveLength(0)
  })

  it('lazy-loads market risk after stock structure and keeps date and broker filters out', async () => {
    const stockStructure = vi.spyOn(api.reports, 'stockStructure').mockResolvedValue({
      summary: { holdingCount: 0, totalEstimatedBuyCost: 0, totalGrossMarketValue: 0, totalEstimatedNetSellValue: 0, totalEstimatedGainLoss: 0, estimatedGainLossPercentage: null },
      insights: [],
      symbolAllocations: [],
      instrumentTypeAllocations: [],
      brokerAllocations: [],
      holdings: [],
      availableBrokers: [],
      availableInstrumentTypes: [],
      generatedAt: '2026-08-06T00:00:00Z',
    })
    const stockValueTrend = vi.spyOn(api.reports, 'stockValueTrend').mockResolvedValue([])
    const marketRisk = vi.spyOn(api.reports, 'stockMarketRisk').mockResolvedValue({
      periodMonths: 12,
      scenarioDescription: '目前持股歷史情境',
      calculationDate: '2026-08-07',
      dataCutoffDate: null,
      portfolioAnnualizedVolatility: { value: null, unavailableReason: 'NoHoldings' },
      eligibleMarketValueCoverage: 0,
      coverageThreshold: 0.9,
      commonObservationCount: 0,
      totalHoldingCount: 0,
      includedInstruments: [],
      excludedInstruments: [],
      volatilityRanking: [],
      correlationMatrix: { labels: [], values: [], commonObservationCount: 0, unavailableReason: 'NoHoldings' },
      syncWarnings: [],
    })
    const wrapper = mountWithAppProviders(ReportsPage, {
      global: { stubs: { Bar: { template: '<div />' }, Line: { template: '<div />' }, Doughnut: { template: '<div />' } } },
    })
    await flushPromises()

    expect(marketRisk).not.toHaveBeenCalled()
    await wrapper.findAll('button').find(button => button.text() === '市場風險')!.trigger('click')
    await flushPromises()

    expect(marketRisk).toHaveBeenCalledWith({ periodMonths: 12 }, expect.anything())
    expect(stockStructure).not.toHaveBeenCalled()
    expect(stockValueTrend).not.toHaveBeenCalled()
    expect(wrapper.findAll('input[type="date"]')).toHaveLength(0)
    expect(wrapper.text()).not.toContain('券商篩選')
  })

  it('clears the previous trend period when the new period fails', async () => {
    const trend = vi.spyOn(api.reports, 'incomeExpenseTrend')
      .mockResolvedValueOnce([{ month: '2026-08', income: 100, expense: 20 }])
      .mockRejectedValueOnce(new Error('new period failed'))
    const wrapper = mountWithAppProviders(ReportsPage, {
      global: { stubs: { Bar: { template: '<div />' }, Line: { template: '<div />' }, Doughnut: { template: '<div />' } } },
    })
    await flushPromises()

    await wrapper.findAll('input[type="date"]')[0].setValue('2026-02-01')
    await flushPromises()

    expect(trend).toHaveBeenCalledTimes(2)
    expect(wrapper.text()).toContain('new period failed')
  })

  it('keeps snapshot list and trend as separate date-range queries', async () => {
    const list = vi.spyOn(api.snapshots, 'list').mockResolvedValue({ items: [snapshot], total: 1, page: 1, pageSize: 15 })
    const trend = vi.spyOn(api.snapshots, 'trend').mockResolvedValue([])
    vi.spyOn(api.snapshots, 'getSchedule').mockResolvedValue({ id: 1, isEnabled: false, frequency: 'Daily', dayOfWeek: null, dayOfMonth: null, timeOfDay: '08:00', lastRunAt: null })
    const wrapper = mountWithAppProviders(SnapshotsPage)
    await flushPromises()

    expect(list).toHaveBeenCalledTimes(1)
    expect(trend).toHaveBeenCalledTimes(1)
    await wrapper.findAll('input[type="date"]')[0].setValue('2026-01-01')
    await flushPromises()

    expect(list).toHaveBeenCalledWith(expect.objectContaining({ dateStart: '2026-01-01' }), expect.anything())
    expect(trend).toHaveBeenCalledWith(expect.objectContaining({ dateStart: '2026-01-01' }), expect.anything())
  })

  it('loads snapshot detail by ID and removes that ID after confirmed deletion', async () => {
    const list = vi.spyOn(api.snapshots, 'list').mockResolvedValue({ items: [snapshot], total: 1, page: 1, pageSize: 15 })
    vi.spyOn(api.snapshots, 'trend').mockResolvedValue([])
    vi.spyOn(api.snapshots, 'get').mockResolvedValue(snapshot)
    vi.spyOn(api.snapshots, 'delete').mockResolvedValue(undefined)
    const wrapper = mountWithAppProviders(SnapshotsPage)
    await flushPromises()

    await wrapper.get('button[title="檢視明細"]').trigger('click')
    await flushPromises()
    expect(api.snapshots.get).toHaveBeenCalledWith(1, expect.objectContaining({ signal: expect.any(AbortSignal) }))

    await wrapper.get('button[title="刪除快照"]').trigger('click')
    wrapper.findComponent(ConfirmDialog).vm.$emit('confirm')
    await flushPromises()
    expect(api.snapshots.delete).toHaveBeenCalledWith(1, expect.anything())
    expect(list).toHaveBeenCalledTimes(2)
  })

  it('uses the comparison route IDs as a query identity', async () => {
    const result = {
      snapshot1: { date: '2026-07-01', name: '七月' },
      snapshot2: { date: '2026-08-01', name: '八月' },
      differences: {
        netWorthBasis: 'AssetsMinusLiabilities' as const,
        netWorth: { old: 1, new: 2, change: 1, changePercent: 100 },
        assets: { old: 1, new: 2, change: 1, changePercent: 100 },
        liabilities: null,
        bankBalance: { old: 1, new: 2, change: 1, changePercent: 100 },
        stockValue: { old: 1, new: 2, change: 1, changePercent: 100 },
        bankDetails: [],
        stockDetails: [],
      },
    }
    const compare = vi.spyOn(api.snapshots, 'compare').mockResolvedValue(result)
    const router = createTestRouter([{ path: '/snapshots/compare', component: ComparePage }])
    await router.push('/snapshots/compare?ids=1,2')
    await router.isReady()
    const wrapper = mount(ComparePage, {
      global: {
        plugins: [router],
        provide: {
          timeZone: { timeZoneId: { value: 'Asia/Taipei' }, isReady: { value: true }, loadError: { value: false }, getToday: () => '2026-08-02', formatDateTime: (value: string) => value },
        },
      },
    })
    await flushPromises()

    expect(compare).toHaveBeenCalledWith(1, 2, expect.objectContaining({ signal: expect.any(AbortSignal) }))
    expect(wrapper.text()).toContain('七月')
  })
})
