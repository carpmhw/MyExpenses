<script setup lang="ts">
import { computed, inject, ref, watch } from 'vue'
import { Line } from 'vue-chartjs'
import type { StockPerformanceMetric, StockPerformanceReport as StockPerformanceReportData } from '../../types'
import { api } from '../../api'
import Card from '../ui/Card.vue'
import QueryState from '../ui/QueryState.vue'
import { useAsyncQuery } from '../../composables/useAsyncQuery'
import { useTimeZone } from '../../composables/useTimeZone'
import { addCalendarYears } from '../../utils/timezone'
import { formatMoney } from '../../utils/format'
import { getThemeColor } from '../../utils/themeColor'

type PerformancePeriod = 'ytd' | '1y' | '3y' | '5y' | 'all'

const timeZone = useTimeZone()
const darkMode = inject<{ isDark: { value: boolean } }>('darkMode', { isDark: ref(false) })
const selectedPeriod = ref<PerformancePeriod>('ytd')

const periodOptions: Array<{ value: PerformancePeriod; label: string }> = [
  { value: 'ytd', label: '今年以來' },
  { value: '1y', label: '近 1 年' },
  { value: '3y', label: '近 3 年' },
  { value: '5y', label: '近 5 年' },
  { value: 'all', label: '全部期間' },
]

// 依系統時區建立績效 API 的日期 contract，避免以瀏覽器 UTC 日期切換期間。
function getPeriodRange(period: PerformancePeriod): { dateStart?: string; dateEnd: string } {
  const dateEnd = timeZone.getToday()
  if (period === 'all') return { dateStart: undefined, dateEnd }
  if (period === 'ytd') return { dateStart: `${dateEnd.slice(0, 4)}-01-01`, dateEnd }
  const years = Number(period.slice(0, -1))
  return { dateStart: addCalendarYears(dateEnd, -years), dateEnd }
}

const performanceQuery = useAsyncQuery<StockPerformanceReportData>({
  key: () => ({ report: 'stock-performance', period: selectedPeriod.value, ...getPeriodRange(selectedPeriod.value) }),
  query: ({ signal }) => api.reports.stockPerformance(getPeriodRange(selectedPeriod.value), { signal }),
  isEmpty: report => report.instrumentBreakdown.length === 0 && report.monthlyPoints.length === 0,
  immediate: false,
})

watch(selectedPeriod, () => { void performanceQuery.refresh() }, { immediate: true })

const report = computed(() => performanceQuery.data.value)

const chartColors = computed(() => ({
  text: getThemeColor('--color-text-secondary', darkMode.isDark.value ? '#B8C0CC' : '#4C566A'),
  primary: getThemeColor('--color-color-info', darkMode.isDark.value ? '#81A1C1' : '#4F759D'),
  grid: getThemeColor('--color-chart-grid', darkMode.isDark.value ? '#4B5563' : '#D2DAE4'),
}))

// 將 backend unavailable reason 轉成可直接呈現給使用者的說明。
function formatUnavailableReason(reason: string): string {
  return {
    NoHoldings: '目前沒有持股',
    NoLedgerHistory: '尚無 Ledger 歷史',
    IncompleteLedgerCoverage: 'Ledger 覆蓋不完整',
    PeriodBeforeTrackingStart: '期間早於績效追蹤起點',
    InsufficientCashFlows: '現金流不足',
    NoCashFlowSignChange: '現金流沒有正負變化',
    MissingTerminalValue: '缺少期末價值',
    NoConvergence: '計算未收斂',
    NonFiniteResult: '結果不是有限數值',
    InsufficientHistoricalPrices: '歷史價格不足',
    ZeroDenominator: '分母為零',
    InvalidPeriod: '期間無效',
  }[reason] ?? '資料不足'
}

// 將一般金額與百分比 metric 統一轉成 KPI 顯示值，null 絕不轉成零。
function formatMetric(metric: StockPerformanceMetric, percentage = false): string {
  if (metric.value === null) return '不可用'
  return percentage ? `${(metric.value * 100).toFixed(2)}%` : formatMoney(metric.value)
}

// 取得 metric 的 unavailable reason；可用數值不顯示誤導性的錯誤文字。
function metricReason(metric: StockPerformanceMetric): string {
  return metric.value === null ? formatUnavailableReason(metric.unavailableReason) : ''
}

const kpis = computed(() => {
  const value = report.value
  if (!value) return []
  return [
    { key: 'currentGrossMarketValue', label: '目前總市值', value: formatMoney(value.summary.currentGrossMarketValue), description: '', reason: '' },
    { key: 'remainingCostBasis', label: '剩餘成本基礎', value: formatMoney(value.summary.remainingCostBasis), description: '', reason: '' },
    { key: 'realizedGainLoss', label: '已實現損益', value: formatMoney(value.summary.realizedGainLoss), description: '', reason: '' },
    { key: 'unrealizedGainLoss', label: '未實現損益', value: formatMoney(value.summary.unrealizedGainLoss), description: '', reason: '' },
    { key: 'netDividendIncome', label: '淨股息收入', value: formatMoney(value.summary.netDividendIncome), description: '', reason: '' },
    { key: 'totalGainLoss', label: '總損益', value: formatMoney(value.summary.totalGainLoss), description: '', reason: '' },
    { key: 'twr', label: 'TWR', value: formatMetric(value.twr, true), description: '排除資金進出時點影響，反映投資組合本身表現。', reason: metricReason(value.twr) },
    { key: 'xirr', label: 'XIRR', value: formatMetric(value.xirr, true), description: '依實際資金投入與取回日期計算的年化報酬。', reason: metricReason(value.xirr) },
  ]
})

const monthlyChartData = computed(() => ({
  labels: report.value?.monthlyPoints.map(point => point.month) ?? [],
  datasets: [{
    label: '累積 TWR',
    data: report.value?.monthlyPoints.map(point => point.cumulativeTwr === null ? null : point.cumulativeTwr * 100) ?? [],
    borderColor: chartColors.value.primary,
    backgroundColor: `${chartColors.value.primary}22`,
    pointRadius: 3,
    tension: 0.25,
    spanGaps: false,
  }],
}))

const monthlyChartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  plugins: {
    legend: { labels: { color: chartColors.value.text } },
  },
  scales: {
    x: { ticks: { color: chartColors.value.text }, grid: { color: 'transparent' } },
    y: {
      ticks: { color: chartColors.value.text, callback: (value: string | number) => `${value}%` },
      grid: { color: chartColors.value.grid },
    },
  },
}))
</script>

<template>
  <div data-testid="stock-performance-report" class="space-y-4">
    <div class="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
      <div>
        <h2 class="text-lg font-semibold text-text-primary">投資績效</h2>
        <p class="mt-1 text-xs text-text-secondary">以 Ledger 現金流與 raw Close 價格計算，不混用預估賣出淨值。</p>
      </div>
      <label class="flex items-center gap-2 text-sm text-text-secondary">
        <span class="sr-only">績效期間</span>
        <select v-model="selectedPeriod" data-testid="performance-period" class="min-h-11 rounded-lg border border-border-strong bg-bg-card px-3 py-2 text-sm text-text-primary focus:border-accent-primary focus:outline-none focus:ring-2 focus:ring-focus-ring">
          <option v-for="option in periodOptions" :key="option.value" :value="option.value">{{ option.label }}</option>
        </select>
      </label>
    </div>

    <Card>
      <QueryState
        :status="performanceQuery.status.value"
        :error-message="performanceQuery.error.value instanceof Error ? performanceQuery.error.value.message : '績效資料載入失敗，請重試。'"
        empty-message="目前沒有足夠的股票 Ledger 資料"
        :last-success-at="performanceQuery.lastSuccessAt.value"
        :retry="performanceQuery.retry"
      >
        <div v-if="report" class="space-y-5">
          <div data-testid="performance-kpis" class="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
            <Card v-for="item in kpis" :key="item.key" class="bg-bg-raised">
              <p class="text-xs text-text-secondary">{{ item.label }}</p>
              <p class="mt-1 text-xl font-semibold text-text-primary">{{ item.value }}</p>
              <p v-if="item.description" class="mt-1 text-xs text-text-tertiary">{{ item.description }}</p>
              <p v-if="item.reason" class="mt-1 text-xs text-color-warning-text">{{ item.reason }}</p>
            </Card>
          </div>
          <p data-testid="performance-return-method-note" class="text-xs text-text-tertiary">TWR 與 XIRR 採用不同計算觀點，數值不同屬正常現象。</p>

          <div v-if="report.hasSyntheticOpeningBalances || report.dataQuality.hasIncompleteLedgerCoverage || report.dataQuality.priceCoverage < 1" data-testid="performance-data-quality" class="space-y-2 rounded-xl border border-color-warning-border bg-color-warning-bg p-4 text-sm text-color-warning-text">
            <p class="font-medium">資料品質與追蹤邊界</p>
            <p v-if="report.hasSyntheticOpeningBalances">包含 synthetic opening；報酬只從 baseline date 開始，系統不推測 acquisition date。</p>
            <p v-if="report.dataQuality.hasIncompleteLedgerCoverage">{{ formatUnavailableReason(report.dataQuality.trackingStartReason) }}</p>
            <p v-if="report.dataQuality.priceCoverage < 1">raw Close 價格覆蓋率：{{ (report.dataQuality.priceCoverage * 100).toFixed(1) }}%</p>
          </div>

          <div class="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1.3fr)_minmax(280px,0.7fr)]">
            <Card>
              <div class="mb-3 flex items-center justify-between gap-3">
                <div>
                  <h3 class="text-sm font-semibold text-text-primary">每月累積 TWR</h3>
                  <p class="mt-1 text-xs text-text-secondary">缺少觀測值時保留 gap，不補零或 forward-fill。</p>
                </div>
                <span class="text-xs text-text-tertiary">{{ report.terminalValuationSource }}</span>
              </div>
              <div v-if="report.monthlyPoints.length > 0" data-testid="performance-monthly-chart" class="h-[280px]">
                <Line :data="monthlyChartData" :options="monthlyChartOptions" />
              </div>
              <div v-else class="flex h-[280px] items-center justify-center text-sm text-text-tertiary">尚無每月績效資料</div>
            </Card>
            <Card>
              <h3 class="text-sm font-semibold text-text-primary">追蹤狀態</h3>
              <dl class="mt-3 space-y-2 text-sm">
                <div class="flex justify-between gap-3"><dt class="text-text-secondary">追蹤起點</dt><dd class="text-right text-text-primary">{{ report.trackingStartDate || '不可用' }}</dd></div>
                <div class="flex justify-between gap-3"><dt class="text-text-secondary">Ledger 覆蓋率</dt><dd class="text-right text-text-primary">{{ formatMetric(report.ledgerCoverage, true) }}</dd></div>
                <div class="flex justify-between gap-3"><dt class="text-text-secondary">價格觀測數</dt><dd class="text-right text-text-primary">{{ report.dataQuality.priceObservationCount }}</dd></div>
                <div class="flex justify-between gap-3"><dt class="text-text-secondary">Ledger 管理標的</dt><dd class="text-right text-text-primary">{{ report.dataQuality.ledgerManagedInstrumentCount }}</dd></div>
              </dl>
            </Card>
          </div>

          <Card>
            <div class="mb-3">
              <h3 class="text-sm font-semibold text-text-primary">標的明細</h3>
              <p class="mt-1 text-xs text-text-secondary">市值變動不等於投資報酬；已結清標的仍保留歷史損益。</p>
            </div>
            <div class="overflow-x-auto">
              <table class="min-w-[940px] w-full text-sm">
                <thead>
                  <tr class="border-b border-border-default">
                    <th class="px-3 py-2 text-left font-medium text-text-secondary">標的</th>
                    <th class="px-3 py-2 text-right font-medium text-text-secondary">目前股數</th>
                    <th class="px-3 py-2 text-right font-medium text-text-secondary">總市值</th>
                    <th class="px-3 py-2 text-right font-medium text-text-secondary">成本基礎</th>
                    <th class="px-3 py-2 text-right font-medium text-text-secondary">已實現</th>
                    <th class="px-3 py-2 text-right font-medium text-text-secondary">未實現</th>
                    <th class="px-3 py-2 text-right font-medium text-text-secondary">股息</th>
                    <th class="px-3 py-2 text-right font-medium text-text-secondary">總損益</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="item in report.instrumentBreakdown" :key="item.stockId" class="border-b border-border-default">
                    <td class="px-3 py-2 text-text-primary">{{ item.name }} <span class="text-text-tertiary">({{ item.symbol }})</span><span v-if="item.isClosed" class="ml-2 text-xs text-text-tertiary">已結清</span></td>
                    <td class="px-3 py-2 text-right text-text-primary">{{ item.currentShares }}</td>
                    <td class="px-3 py-2 text-right text-text-primary">{{ formatMoney(item.grossMarketValue) }}</td>
                    <td class="px-3 py-2 text-right text-text-primary">{{ formatMoney(item.remainingCostBasis) }}</td>
                    <td class="px-3 py-2 text-right" :class="item.realizedGainLoss >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">{{ formatMoney(item.realizedGainLoss) }}</td>
                    <td class="px-3 py-2 text-right" :class="item.unrealizedGainLoss >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">{{ formatMoney(item.unrealizedGainLoss) }}</td>
                    <td class="px-3 py-2 text-right text-text-primary">{{ formatMoney(item.dividendIncome) }}</td>
                    <td class="px-3 py-2 text-right font-medium" :class="item.totalGainLoss >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">{{ formatMoney(item.totalGainLoss) }}</td>
                  </tr>
                </tbody>
              </table>
            </div>
          </Card>
        </div>
      </QueryState>
    </Card>
  </div>
</template>
