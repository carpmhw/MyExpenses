import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ReportsPage from '../../src/pages/reports/index.vue'
import { api } from '../../src/api'
import { mountWithAppProviders } from '../support/render'

async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await Promise.resolve()
}

describe('ReportsPage investment performance tab', () => {
  afterEach(() => vi.restoreAllMocks())

  // 驗證投資績效 tab 維持局部水平捲動，且元件只在 tab 被選取後查詢。
  it('lazy-loads the component-owned performance query', async () => {
    vi.spyOn(api.reports, 'incomeExpenseTrend').mockResolvedValue([])
    const performance = vi.spyOn(api.reports, 'stockPerformance').mockResolvedValue({
      dateStart: '2026-01-01',
      dateEnd: '2026-08-25',
      trackingStartDate: null,
      hasSyntheticOpeningBalances: false,
      terminalValuationSource: 'CurrentGrossMarketValue',
      ledgerCoverage: { value: null, unavailableReason: 'NoHoldings' },
      summary: { currentGrossMarketValue: 0, remainingCostBasis: 0, realizedGainLoss: 0, unrealizedGainLoss: 0, netDividendIncome: 0, totalGainLoss: 0 },
      twr: { value: null, unavailableReason: 'NoHoldings' },
      xirr: { value: null, unavailableReason: 'NoHoldings' },
      monthlyPoints: [],
      instrumentBreakdown: [],
      dataQuality: { activeInstrumentCount: 0, ledgerManagedInstrumentCount: 0, priceObservationCount: 0, priceCoverage: 0, trackingStartReason: 'NoHoldings', hasIncompleteLedgerCoverage: false },
    })

    const wrapper = mountWithAppProviders(ReportsPage)
    await flushPromises()
    expect(performance).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="report-tabs"]').attributes('class')).toContain('overflow-x-auto')

    await wrapper.get('[data-testid="report-tab-stockPerformance"]').trigger('click')
    await flushPromises()
    expect(performance).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('投資績效')
  })
})
