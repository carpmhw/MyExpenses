<script setup lang="ts">
import { computed, inject, onMounted, ref, watch } from 'vue'
import { Line } from 'vue-chartjs'
import { CategoryScale, Chart as ChartJS, Filler, Legend, LinearScale, LineElement, PointElement, Tooltip } from 'chart.js'
import { api } from '../../api'
import type { StockMarketRiskMetric, StockMarketRiskReport, StockMarketRiskUnavailableReason, StockPerformanceReport, StockStructureReport, StockValueTrendPoint } from '../../types'
import Card from '../ui/Card.vue'
import QueryState from '../ui/QueryState.vue'
import { useAsyncQuery } from '../../composables/useAsyncQuery'
import { formatMoney } from '../../utils/format'
import { getThemeColor } from '../../utils/themeColor'
import { getCurrentYearRange } from '../../utils/timezone'
import { useTimeZone } from '../../composables/useTimeZone'

ChartJS.register(CategoryScale, LinearScale, PointElement, LineElement, Tooltip, Legend, Filler)

const emit = defineEmits<{
  navigate: [target: 'stockPerformance' | 'stockStructure' | 'marketRisk']
}>()

const valueTrendMonths = ref<6 | 12 | 24 | 36 | 60>(12)
const darkMode = inject<{ isDark: { value: boolean } }>('darkMode') ?? { isDark: ref(false) }
const timeZone = useTimeZone()

const structureQuery = useAsyncQuery<StockStructureReport>({
  key: () => ({ report: 'stock-structure' }),
  query: ({ signal }) => api.reports.stockStructure({}, { signal }),
  isEmpty: data => data.holdings.length === 0,
  immediate: false,
})

const riskQuery = useAsyncQuery<StockMarketRiskReport>({
  key: () => ({ report: 'stock-market-risk', periodMonths: 12 }),
  query: ({ signal }) => api.reports.stockMarketRisk({ periodMonths: 12 }, { signal }),
  isEmpty: data => data.totalHoldingCount === 0,
  immediate: false,
})

const valueTrendQuery = useAsyncQuery<StockValueTrendPoint[]>({
  key: () => ({ report: 'stock-value-trend', months: valueTrendMonths.value }),
  query: ({ signal }) => api.reports.stockValueTrend({ months: valueTrendMonths.value }, { signal }),
  isEmpty: data => data.length === 0,
  immediate: false,
})

const performanceQuery = useAsyncQuery<StockPerformanceReport>({
  key: () => {
    const range = getCurrentYearRange(new Date(), timeZone.timeZoneId.value)
    return { report: 'stock-performance-summary', dateStart: range.start, dateEnd: range.end }
  },
  query: ({ signal }) => {
    const range = getCurrentYearRange(new Date(), timeZone.timeZoneId.value)
    return api.reports.stockPerformance({ dateStart: range.start, dateEnd: range.end }, { signal })
  },
  isEmpty: data => data.instrumentBreakdown.length === 0 && data.monthlyPoints.length === 0,
  immediate: false,
})

const structureData = computed(() => structureQuery.data.value)
const riskData = computed(() => riskQuery.data.value)
const valueTrendData = computed(() => valueTrendQuery.data.value ?? [])
const performanceData = computed(() => performanceQuery.data.value)
const topRiskContributions = computed(() => [...(riskData.value?.riskContributions ?? [])]
  .sort((left, right) => right.contributionPercentage - left.contributionPercentage)
  .slice(0, 5))
const chartColors = computed(() => {
  const theme = darkMode.isDark.value ? 'dark' : 'light'
  return {
    text: getThemeColor('--color-text-secondary', theme === 'dark' ? '#B8C0CC' : '#4C566A'),
    primary: getThemeColor('--color-text-primary', theme === 'dark' ? '#ECEFF4' : '#2E3440'),
    surface: getThemeColor('--color-bg-card', theme === 'dark' ? '#3B4252' : '#F8FAFC'),
    accent: getThemeColor('--color-color-info', theme === 'dark' ? '#81A1C1' : '#4F759D'),
  }
})
const valueTrendChartData = computed(() => ({
  labels: valueTrendData.value.map(point => point.month),
  datasets: [{
    label: '全部持股預估賣出淨值',
    data: valueTrendData.value.map(point => point.totalStockValue),
    borderColor: chartColors.value.accent,
    backgroundColor: `${chartColors.value.accent}22`,
    fill: true,
    tension: 0.35,
  }],
}))
const valueTrendChartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  plugins: {
    legend: { labels: { color: chartColors.value.text } },
    tooltip: {
      backgroundColor: chartColors.value.surface,
      titleColor: chartColors.value.primary,
      bodyColor: chartColors.value.primary,
    },
  },
}))

// 將查詢例外轉換為可安全顯示的重試訊息。
function queryErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : '載入資料失敗，請重試。'
}

// 將結構報表百分比轉為顯示文字，避免把不可計算誤當成零。
function formatStructurePercentage(value: number | null): string {
  return value === null ? '不可用' : `${value.toFixed(1)}%`
}

// 將風險比例轉為百分比文字，並保留風險貢獻的負值。
function formatRiskPercentage(value: number | null | undefined): string {
  return value === null || value === undefined ? '不可用' : `${(value * 100).toFixed(1)}%`
}

// 將可正可負的風險貢獻保留明確符號，避免正值與無方向比例混淆。
function formatSignedRiskPercentage(value: number): string {
  return `${value > 0 ? '+' : ''}${(value * 100).toFixed(1)}%`
}

// 將績效 metric 轉成總覽摘要的百分比，並保留後端 unavailable reason。
function formatPerformanceMetric(value: StockPerformanceReport['twr']): string {
  if (value.value === null) return `不可用：${formatPerformanceReason(value.unavailableReason)}`
  return `${(value.value * 100).toFixed(1)}%`
}

// 將績效摘要的 unavailable reason 轉成簡短繁體中文。
function formatPerformanceReason(reason: string): string {
  return {
    NoHoldings: '尚無目前持股',
    NoLedgerHistory: '尚無 Ledger 歷史',
    IncompleteLedgerCoverage: 'Ledger 覆蓋不完整',
    InsufficientHistoricalPrices: '歷史價格不足',
    PeriodBeforeTrackingStart: '早於追蹤起點',
  }[reason] ?? '資料不足'
}

// 將資料品質 UTC 時間轉為簡短文字，並清楚標示缺少的更新時間。
function formatDataQualityTime(value: string | null): string {
  return value ? value.replace('T', ' ').replace('Z', ' UTC') : '尚無更新時間'
}

// 將風險 metric 呈現為數值或後端提供的不可用原因。
function formatMetric(metric: StockMarketRiskMetric | undefined): string {
  if (!metric || metric.value === null)
    return `不可用：${formatReason(metric?.unavailableReason)}`
  return formatRiskPercentage(metric.value)
}

// 為零波動的已知除零情境提供風險貢獻專用說明。
function riskContributionEmptyMessage(metric: StockMarketRiskMetric): string {
  if (metric.value === 0)
    return '組合波動度為 0，無法計算風險貢獻。'
  return `尚無可用風險貢獻；${formatReason(metric.unavailableReason)}。`
}

// 將 typed unavailable reason 轉成使用者可理解的繁體中文。
function formatReason(reason: StockMarketRiskUnavailableReason | null | undefined): string {
  const labels: Record<StockMarketRiskUnavailableReason, string> = {
    NoHoldings: '尚無目前持股',
    UnknownMarket: '市場待辨識',
    BlankSymbol: '代號空白',
    NonPositiveGrossValue: '毛市值不是正值',
    InsufficientHistory: '資料不足',
    NoEligibleInstruments: '沒有合格標的',
    CoverageBelowThreshold: '覆蓋不足',
    InsufficientCommonDates: '共同交易日不足',
    NotEnoughEligibleInstruments: '合格標的不滿兩檔',
    NonFiniteResult: '統計結果無效',
    InvalidPeriod: '觀察期無效',
  }
  return reason ? labels[reason] : '資料準備中'
}

// 載入三個彼此獨立的總覽資料來源，讓單一失敗不影響其他區塊。
function loadInitialData(): void {
  void structureQuery.refresh()
  void riskQuery.refresh()
  void valueTrendQuery.refresh()
  void performanceQuery.refresh()
}

watch(valueTrendMonths, () => {
  void valueTrendQuery.refresh()
})

onMounted(loadInitialData)
</script>

<template>
  <div class="space-y-6">
    <div class="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
      <div>
        <h2 class="text-base font-semibold text-text-primary">股票總覽</h2>
        <p class="mt-1 text-xs text-text-secondary">整合目前持股估值與本機歷史風險；持股價值趨勢不等同投資績效。</p>
      </div>
      <div class="flex gap-2">
        <button data-testid="navigate-stock-performance" type="button" class="rounded-lg border border-border-strong px-3 py-2 text-sm text-text-primary" @click="emit('navigate', 'stockPerformance')">查看投資績效</button>
        <button data-testid="navigate-stock-structure" type="button" class="rounded-lg border border-border-strong px-3 py-2 text-sm text-text-primary" @click="emit('navigate', 'stockStructure')">查看持股結構</button>
        <button data-testid="navigate-market-risk" type="button" class="rounded-lg border border-border-strong px-3 py-2 text-sm text-text-primary" @click="emit('navigate', 'marketRisk')">查看市場風險</button>
      </div>
    </div>

    <QueryState :status="performanceQuery.status.value" :error-message="queryErrorMessage(performanceQuery.error.value)" :empty-message="'尚無可用投資績效摘要'" :last-success-at="performanceQuery.lastSuccessAt.value" :retry="performanceQuery.retry">
      <Card v-if="performanceData" data-testid="overview-performance-summary">
        <div class="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <h3 class="text-sm font-semibold text-text-primary">投資績效摘要</h3>
            <p class="mt-1 text-xs text-text-secondary">今年以來 · 以 Ledger 與 raw Close 計算</p>
          </div>
          <button type="button" class="rounded-lg border border-border-strong px-3 py-2 text-sm text-text-primary" @click="emit('navigate', 'stockPerformance')">查看完整績效</button>
        </div>
        <div class="mt-4 grid grid-cols-1 gap-3 sm:grid-cols-3">
          <div>
            <p class="text-xs text-text-secondary">總損益</p>
            <p class="mt-1 text-lg font-semibold" :class="performanceData.summary.totalGainLoss >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">{{ formatMoney(performanceData.summary.totalGainLoss) }}</p>
          </div>
          <div>
            <p class="text-xs text-text-secondary">TWR</p>
            <p class="mt-1 text-lg font-semibold text-text-primary">{{ formatPerformanceMetric(performanceData.twr) }}</p>
          </div>
          <div>
            <p class="text-xs text-text-secondary">XIRR</p>
            <p class="mt-1 text-lg font-semibold text-text-primary">{{ formatPerformanceMetric(performanceData.xirr) }}</p>
          </div>
        </div>
      </Card>
    </QueryState>

    <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-6">
      <QueryState :status="structureQuery.status.value" :error-message="queryErrorMessage(structureQuery.error.value)" :empty-message="'尚無目前持股'" :last-success-at="structureQuery.lastSuccessAt.value" :retry="structureQuery.retry">
        <Card v-if="structureData"><p class="text-xs text-text-secondary">預估賣出淨值</p><p class="mt-2 text-xl font-bold text-text-primary">{{ formatMoney(structureData.summary.totalEstimatedNetSellValue) }}</p></Card>
      </QueryState>
      <QueryState :status="structureQuery.status.value" :error-message="queryErrorMessage(structureQuery.error.value)" :empty-message="'尚無目前持股'" :last-success-at="structureQuery.lastSuccessAt.value" :retry="structureQuery.retry">
        <Card v-if="structureData"><p class="text-xs text-text-secondary">預估未實現損益</p><p class="mt-2 text-xl font-bold" :class="structureData.summary.totalEstimatedGainLoss >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">{{ formatMoney(structureData.summary.totalEstimatedGainLoss) }}</p></Card>
      </QueryState>
      <QueryState :status="structureQuery.status.value" :error-message="queryErrorMessage(structureQuery.error.value)" :empty-message="'尚無目前持股'" :last-success-at="structureQuery.lastSuccessAt.value" :retry="structureQuery.retry">
        <Card v-if="structureData"><p class="text-xs text-text-secondary">預估損益率</p><p class="mt-2 text-xl font-bold text-text-primary">{{ formatStructurePercentage(structureData.summary.estimatedGainLossPercentage) }}</p></Card>
      </QueryState>
      <QueryState :status="structureQuery.status.value" :error-message="queryErrorMessage(structureQuery.error.value)" :empty-message="'尚無目前持股'" :last-success-at="structureQuery.lastSuccessAt.value" :retry="structureQuery.retry">
        <Card v-if="structureData"><p class="text-xs text-text-secondary">Top 1 占比</p><p class="mt-2 text-xl font-bold text-text-primary">{{ formatStructurePercentage(structureData.concentration.top1Percentage) }}</p></Card>
      </QueryState>
      <QueryState :status="riskQuery.status.value" :error-message="queryErrorMessage(riskQuery.error.value)" :empty-message="'尚無目前持股風險資料'" :last-success-at="riskQuery.lastSuccessAt.value" :retry="riskQuery.retry">
        <Card v-if="riskData"><p class="text-xs text-text-secondary">12M 年化波動度</p><p class="mt-2 text-xl font-bold text-text-primary">{{ formatMetric(riskData.portfolioAnnualizedVolatility) }}</p></Card>
      </QueryState>
      <QueryState :status="riskQuery.status.value" :error-message="queryErrorMessage(riskQuery.error.value)" :empty-message="'尚無目前持股風險資料'" :last-success-at="riskQuery.lastSuccessAt.value" :retry="riskQuery.retry">
        <Card v-if="riskData"><p class="text-xs text-text-secondary">12M 最大回撤</p><p class="mt-2 text-xl font-bold text-text-primary">{{ formatMetric(riskData.portfolioMaximumDrawdown) }}</p></Card>
      </QueryState>
    </div>

    <QueryState :status="structureQuery.status.value" :error-message="queryErrorMessage(structureQuery.error.value)" :empty-message="'尚無目前持股'" :last-success-at="structureQuery.lastSuccessAt.value" :retry="structureQuery.retry">
      <div v-if="structureData" class="grid grid-cols-1 gap-6 xl:grid-cols-3">
        <Card><h3 class="text-sm font-semibold text-text-primary">市場配置</h3><p v-if="structureData.marketAllocations.length === 0" class="mt-3 text-sm text-text-tertiary">暫無可顯示的市場配置。</p><dl v-else class="mt-3 space-y-2 text-sm"><div v-for="item in structureData.marketAllocations" :key="item.key" class="flex justify-between gap-3"><dt class="text-text-secondary">{{ item.label }}</dt><dd class="font-semibold text-text-primary">{{ formatStructurePercentage(item.percentage) }}</dd></div></dl></Card>
        <Card><h3 class="text-sm font-semibold text-text-primary">商品類型摘要</h3><p v-if="structureData.instrumentTypeAllocations.length === 0" class="mt-3 text-sm text-text-tertiary">暫無可顯示的商品類型摘要。</p><dl v-else class="mt-3 space-y-2 text-sm"><div v-for="item in structureData.instrumentTypeAllocations" :key="item.key" class="flex justify-between gap-3"><dt class="text-text-secondary">{{ item.label }}</dt><dd class="font-semibold text-text-primary">{{ formatStructurePercentage(item.percentage) }}</dd></div></dl></Card>
        <Card><h3 class="text-sm font-semibold text-text-primary">集中度</h3><dl class="mt-3 grid grid-cols-2 gap-3 text-sm"><div><dt class="text-text-tertiary">Top 3</dt><dd class="font-semibold text-text-primary">{{ formatStructurePercentage(structureData.concentration.top3Percentage) }}</dd></div><div><dt class="text-text-tertiary">Top 5</dt><dd class="font-semibold text-text-primary">{{ formatStructurePercentage(structureData.concentration.top5Percentage) }}</dd></div><div><dt class="text-text-tertiary">HHI</dt><dd class="font-semibold text-text-primary">{{ structureData.concentration.hhi?.toFixed(3) ?? '不可用' }}</dd></div><div><dt class="text-text-tertiary">有效持股數</dt><dd class="font-semibold text-text-primary">{{ structureData.concentration.effectiveHoldingCount?.toFixed(1) ?? '不可用' }}</dd></div></dl></Card>
      </div>
    </QueryState>

    <Card>
      <div class="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between"><div><h3 class="text-sm font-semibold text-text-primary">全部持股價值趨勢</h3><p class="mt-1 text-xs text-text-tertiary">歷史快照資產價值，不等同投資報酬率</p></div><div class="flex rounded-lg bg-bg-raised p-1" role="group" aria-label="總覽持股價值趨勢期間"><button v-for="months in [6, 12, 24, 36, 60] as const" :key="months" :data-testid="`overview-value-trend-period-${months}`" type="button" class="rounded-md px-2 py-1 text-xs text-text-secondary" :class="valueTrendMonths === months ? 'bg-bg-active text-text-primary shadow-sm' : ''" :aria-pressed="valueTrendMonths === months" @click="valueTrendMonths = months">{{ months }}M</button></div></div>
      <QueryState :status="valueTrendQuery.status.value" :error-message="queryErrorMessage(valueTrendQuery.error.value)" :empty-message="'尚無全部持股價值歷史'" :last-success-at="valueTrendQuery.lastSuccessAt.value" :retry="valueTrendQuery.retry"><div v-if="valueTrendData.length === 1" class="flex h-[280px] items-center justify-center text-sm text-text-tertiary">目前只有 1 筆全部持股價值快照</div><div v-else class="h-[280px]"><Line :data="valueTrendChartData" :options="valueTrendChartOptions" /></div></QueryState>
    </Card>

    <QueryState :status="riskQuery.status.value" :error-message="queryErrorMessage(riskQuery.error.value)" :empty-message="'尚無目前持股風險資料'" :last-success-at="riskQuery.lastSuccessAt.value" :retry="riskQuery.retry">
      <Card v-if="riskData"><div class="flex items-center justify-between gap-3"><h3 class="text-sm font-semibold text-text-primary">Top 5 風險貢獻</h3><span class="text-xs text-text-tertiary">負值代表分散效果</span></div><p v-if="topRiskContributions.length === 0" class="py-6 text-center text-sm text-text-tertiary">{{ riskContributionEmptyMessage(riskData.portfolioAnnualizedVolatility) }}</p><div v-else class="mt-4 space-y-3"><div v-for="item in topRiskContributions" :key="`${item.market}-${item.symbol}-${item.name}`" class="flex justify-between gap-3 text-sm"><span class="min-w-0 truncate text-text-primary">{{ item.name }} ({{ item.symbol || '無代號' }})</span><span class="shrink-0 font-semibold" :class="item.contributionPercentage < 0 ? 'text-color-info' : 'text-text-primary'">{{ formatSignedRiskPercentage(item.contributionPercentage) }}</span></div></div></Card>
    </QueryState>

    <QueryState :status="structureQuery.status.value" :error-message="queryErrorMessage(structureQuery.error.value)" :empty-message="'尚無目前持股'" :last-success-at="structureQuery.lastSuccessAt.value" :retry="structureQuery.retry">
      <Card v-if="structureData" data-testid="overview-data-quality-warning" :title="`超過 ${structureData.dataQuality.staleAfterHours} 小時僅為資料新鮮度提示，非交易日曆判斷或行情正確性判定。`" :class="structureData.dataQuality.missingLastPriceUpdateCount > 0 || structureData.dataQuality.stalePriceCount > 0 ? 'border-color-warning-border bg-color-warning-bg text-color-warning-text' : ''"><h3 class="text-sm font-semibold text-text-primary">價格資料品質</h3><div class="mt-3 grid grid-cols-2 gap-3 text-sm"><p class="text-text-secondary">價格覆蓋 <span class="font-semibold text-text-primary">{{ structureData.dataQuality.positivePriceCoverage === null ? '不可用' : formatRiskPercentage(structureData.dataQuality.positivePriceCoverage) }}</span></p><p class="text-text-secondary">缺少更新 <span class="font-semibold text-text-primary">{{ structureData.dataQuality.missingLastPriceUpdateCount }} 筆</span></p><p class="text-text-secondary">最舊更新 <span class="font-semibold text-text-primary">{{ formatDataQualityTime(structureData.dataQuality.oldestLastPriceUpdateUtc) }}</span></p><p class="text-text-secondary">最新更新 <span class="font-semibold text-text-primary">{{ formatDataQualityTime(structureData.dataQuality.latestLastPriceUpdateUtc) }}</span></p><p class="text-text-secondary">行情截止日 <span class="font-semibold text-text-primary">{{ riskData?.dataCutoffDate ?? '不可用' }}</span></p><p class="text-text-secondary">行情市值覆蓋率 <span class="font-semibold text-text-primary">{{ riskData ? formatMetric(riskData.eligibleMarketValueCoverageMetric) : '不可用' }}</span></p></div><p class="mt-3 text-xs" :class="structureData.dataQuality.missingLastPriceUpdateCount > 0 || structureData.dataQuality.stalePriceCount > 0 ? 'text-color-warning-text' : 'text-text-tertiary'">{{ structureData.dataQuality.stalePriceCount }} 筆超過 {{ structureData.dataQuality.staleAfterHours }} 小時；此為資料新鮮度提示，非行情正確性判定。</p></Card>
    </QueryState>
  </div>
</template>
