import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createMemoryHistory, createRouter } from 'vue-router'
import SchedulesPage from '../../src/pages/schedules/index.vue'
import { createFetchMock, deferred, jsonResponse } from '../support/deferred'

async function flushPromises(): Promise<void> {
  await Promise.resolve()
  await Promise.resolve()
  await new Promise(resolve => setTimeout(resolve, 0))
}

describe('schedules page', () => {
  it('renders three backend-driven schedule cards and a responsive history surface', async () => {
    const overview = [
      {
        jobKey: 'AutomaticSnapshot', displayName: '自動財務快照', configurationSource: 'AutoSnapshotConfig',
        isEnabled: false, frequencyDescription: '每日 08:00', scheduleTimeZoneId: 'Asia/Taipei', nextRunAtUtc: null, latestExecution: null,
      },
      {
        jobKey: 'StockPriceUpdate', displayName: '目前股價更新', configurationSource: '固定市場排程',
        isEnabled: true, frequencyDescription: '台灣平日 23:00', scheduleTimeZoneId: 'Asia/Taipei', nextRunAtUtc: '2026-08-08T15:00:00Z', latestExecution: null,
      },
      {
        jobKey: 'HistoricalMarketDataSync', displayName: '歷史行情同步', configurationSource: '固定市場排程',
        isEnabled: true, frequencyDescription: '台灣平日 23:30', scheduleTimeZoneId: 'Asia/Taipei', nextRunAtUtc: '2026-08-08T15:30:00Z', latestExecution: null,
      },
    ]
    const historyItem = {
      id: 1, jobKey: 'StockPriceUpdate', scheduledForUtc: '2026-08-08T15:00:00Z', scheduleTimeZoneId: 'Asia/Taipei',
      scheduledLocalDate: '2026-08-08', status: 'Succeeded', startedAtUtc: '2026-08-08T15:00:01Z', completedAtUtc: '2026-08-08T15:00:02Z',
      attemptCount: 1, targetCount: 1, succeededCount: 1, failedCount: 0, affectedCount: 1, resultCode: 'Completed', safeMessage: '完成',
    }
    createFetchMock(input => String(input).includes('/schedules/executions')
      ? jsonResponse({ items: [historyItem], total: 1, page: 1, pageSize: 20 })
      : jsonResponse(overview))
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/schedules', component: SchedulesPage }, { path: '/snapshots', component: { template: '<div />' } }],
    })
    await router.push('/schedules')
    await router.isReady()

    const wrapper = mount(SchedulesPage, { global: { plugins: [router] } })
    await flushPromises()

    expect(wrapper.find('[aria-label="排程總覽"]').findAll('.bg-bg-card')).toHaveLength(3)
    expect(wrapper.text()).toContain('執行歷史')
    expect(wrapper.findAll('[class*="md:hidden"]').length).toBeGreaterThan(0)
    wrapper.unmount()
  })

  it('keeps overview and history failures independent', async () => {
    const fetchMock = createFetchMock(input => String(input).includes('/schedules/executions')
      ? jsonResponse({ items: [], total: 0, page: 1, pageSize: 20 })
      : jsonResponse({ detail: '總覽失敗' }, 500))
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/schedules', component: SchedulesPage }],
    })
    await router.push('/schedules')
    await router.isReady()

    const wrapper = mount(SchedulesPage, { global: { plugins: [router] } })
    await flushPromises()

    expect(wrapper.text()).toContain('總覽失敗')
    expect(wrapper.text()).toContain('執行歷史')
    expect(fetchMock).toHaveBeenCalled()
    wrapper.unmount()
    vi.restoreAllMocks()
  })

  it('polls visible queries every 30 seconds and aborts active requests when hidden', async () => {
    vi.useFakeTimers()
    const pending = deferred<Response>()
    const signals: AbortSignal[] = []
    let callCount = 0
    createFetchMock((input, init) => {
      if (init?.signal) signals.push(init.signal)
      callCount++
      if (callCount <= 2) {
        return String(input).includes('/schedules/executions')
          ? jsonResponse({ items: [], total: 0, page: 1, pageSize: 20 })
          : jsonResponse([])
      }
      return pending.promise
    })
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/schedules', component: SchedulesPage }],
    })
    await router.push('/schedules')
    await router.isReady()
    const wrapper = mount(SchedulesPage, { global: { plugins: [router] } })
    await Promise.resolve()
    await Promise.resolve()

    await vi.advanceTimersByTimeAsync(30_000)
    expect(callCount).toBe(4)
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' })
    document.dispatchEvent(new Event('visibilitychange'))
    expect(signals[2]?.aborted).toBe(true)
    expect(signals[3]?.aborted).toBe(true)
    pending.resolve(jsonResponse([]))
    wrapper.unmount()
    vi.useRealTimers()
  })
})
