<script setup lang="ts">
import { computed, inject, onMounted, ref, watch } from 'vue'
import { Bar, Doughnut, Line } from 'vue-chartjs'
import {
  ArcElement,
  BarElement,
  CategoryScale,
  Chart as ChartJS,
  Filler,
  Legend,
  LinearScale,
  LineElement,
  PointElement,
  Title,
  Tooltip,
} from 'chart.js'
import { api } from '../../api'
import type {
  StockInstrumentType,
  StockStructureAllocation,
  StockStructureReport as StockStructureReportData,
  StockValueTrendPoint,
} from '../../types'
import Card from '../ui/Card.vue'
import QueryState from '../ui/QueryState.vue'
import { useAsyncQuery } from '../../composables/useAsyncQuery'
import { formatMoney, formatShares } from '../../utils/format'
import { formatStockInstrumentType } from '../../utils/stock'
import { getThemeColor } from '../../utils/themeColor'

ChartJS.register(
  CategoryScale,
  LinearScale,
  BarElement,
  PointElement,
  LineElement,
  ArcElement,
  Title,
  Tooltip,
  Legend,
  Filler,
)

const darkMode = inject<{ isDark: { value: boolean } }>('darkMode') ?? { isDark: ref(false) }
const selectedBroker = ref('')
const selectedInstrumentType = ref<StockInstrumentType | ''>('')
const valueTrendMonths = ref<6 | 12 | 24 | 36 | 60>(12)

const structureQuery = useAsyncQuery<StockStructureReportData>({
  key: () => ({
    report: 'stock-structure',
    broker: selectedBroker.value,
    instrumentType: selectedInstrumentType.value,
  }),
  query: ({ signal }) => api.reports.stockStructure({
    broker: selectedBroker.value,
    instrumentType: selectedInstrumentType.value || undefined,
  }, { signal }),
  isEmpty: data => data.holdings.length === 0,
  immediate: false,
})

const valueTrendQuery = useAsyncQuery<StockValueTrendPoint[]>({
  key: () => ({ report: 'stock-value-trend', months: valueTrendMonths.value }),
  query: ({ signal }) => api.reports.stockValueTrend({ months: valueTrendMonths.value }, { signal }),
  isEmpty: data => data.length === 0,
  immediate: false,
})

const structureData = computed(() => structureQuery.data.value)
const valueTrendData = computed(() => valueTrendQuery.data.value ?? [])

const chartColors = computed(() => {
  const theme = darkMode.isDark.value ? 'dark' : 'light'
  return {
    text: getThemeColor('--color-text-secondary', theme === 'dark' ? '#B8C0CC' : '#4C566A'),
    primary: getThemeColor('--color-text-primary', theme === 'dark' ? '#ECEFF4' : '#2E3440'),
    grid: getThemeColor('--color-chart-grid', theme === 'dark' ? '#4B5563' : '#D2DAE4'),
    surface: getThemeColor('--color-bg-card', theme === 'dark' ? '#3B4252' : '#F8FAFC'),
    positive: getThemeColor('--color-color-income-chart', theme === 'dark' ? '#A3BE8C' : '#6F8F5E'),
    negative: getThemeColor('--color-color-expense-chart', theme === 'dark' ? '#E6A5AB' : '#AA4F5A'),
    accent: getThemeColor('--color-color-info', theme === 'dark' ? '#81A1C1' : '#4F759D'),
    warning: getThemeColor('--color-color-warning', theme === 'dark' ? '#EBCB8B' : '#A87522'),
  }
})

const allocationPalette = ['#81A1C1', '#A3BE8C', '#EBCB8B', '#B48EAD', '#88C0D0', '#D08770', '#5E81AC', '#BF616A']

const instrumentChartData = computed(() => createAllocationChartData(
  structureData.value?.instrumentTypeAllocations ?? [],
))

const brokerChartData = computed(() => createAllocationChartData(
  structureData.value?.brokerAllocations ?? [],
))

const marketChartData = computed(() => createAllocationChartData(
  structureData.value?.marketAllocations ?? [],
))

const symbolChartData = computed(() => ({
  labels: (structureData.value?.symbolAllocations ?? []).slice(0, 10).map(allocation => allocation.label),
  datasets: [{
    label: '配置金額',
    data: (structureData.value?.symbolAllocations ?? []).slice(0, 10).map(allocation => allocation.value),
    backgroundColor: chartColors.value.accent,
    borderColor: chartColors.value.accent,
    borderWidth: 1,
    borderRadius: 4,
  }],
}))

const valueTrendChartData = computed(() => ({
  labels: valueTrendData.value.map(point => point.month),
  datasets: [{
    label: '全部持股預估賣出淨值',
    data: valueTrendData.value.map(point => point.totalStockValue),
    borderColor: chartColors.value.positive,
    backgroundColor: `${chartColors.value.positive}22`,
    fill: true,
    tension: 0.35,
    pointRadius: 4,
  }],
}))

const chartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  plugins: {
    legend: { labels: { color: chartColors.value.text } },
    tooltip: {
      backgroundColor: chartColors.value.surface,
      titleColor: chartColors.value.primary,
      bodyColor: chartColors.value.primary,
      callbacks: {
        label: (context: { dataset: { label?: string }; parsed: { y?: number | null } }) =>
          `${context.dataset.label ?? ''}: ${formatMoney(context.parsed.y ?? 0)}`,
      },
    },
  },
  scales: {
    x: { ticks: { color: chartColors.value.text }, grid: { color: 'transparent' } },
    y: { ticks: { color: chartColors.value.text, callback: (value: string | number) => formatMoney(Number(value)) }, grid: { color: chartColors.value.grid } },
  },
}))

const canRenderAllocations = computed(() =>
  (structureData.value?.summary.totalEstimatedNetSellValue ?? 0) > 0)

// 將配置資料轉成 Doughnut 圖表需要的資料結構。
function createAllocationChartData(allocations: StockStructureAllocation[]) {
  return {
    labels: allocations.map(allocation => allocation.label),
    datasets: [{
      data: allocations.map(allocation => allocation.value),
      backgroundColor: allocations.map((_, index) => allocationPalette[index % allocationPalette.length]),
      borderColor: chartColors.value.surface,
      borderWidth: 1,
    }],
  }
}

// 建立指定配置資料使用的 Doughnut 圖表選項與 tooltip。
function createDoughnutOptions(allocations: StockStructureAllocation[]) {
  return {
    responsive: true,
    maintainAspectRatio: false,
    plugins: {
      legend: { position: 'right' as const, labels: { color: chartColors.value.text, padding: 12 } },
      tooltip: {
        backgroundColor: chartColors.value.surface,
        titleColor: chartColors.value.primary,
        bodyColor: chartColors.value.primary,
        callbacks: {
          label: (context: { label?: string; parsed: number }) => {
            const allocation = allocations.find(item => item.label === context.label)
            return `${context.label ?? ''}: ${formatMoney(context.parsed)} (${formatPercentage(allocation?.percentage ?? null)})`
          },
        },
      },
    },
  }
}

// 將查詢錯誤轉換成不暴露內部資訊的畫面訊息。
function queryErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : '載入資料失敗，請重試。'
}

// 將可為 null 的百分比轉成報表顯示格式。
function formatPercentage(value: number | null): string {
  return value === null ? '無法計算' : `${value.toFixed(1)}%`
}

// 將查詢資料的百分比轉成簡短明細文字。
function formatAllocation(value: number | null): string {
  return formatPercentage(value)
}

// 將 ISO UTC 時間轉為資料品質摘要使用的簡短顯示文字。
function formatDataQualityTime(value: string | null): string {
  return value ? value.replace('T', ' ').replace('Z', ' UTC') : '無更新時間'
}

// 將空白代號明確標示記錄身份，避免同名持股在明細中無法區分。
function formatHoldingLabel(name: string, symbol: string, id: number): string {
  const normalizedSymbol = symbol.trim()
  return normalizedSymbol ? `${name} (${normalizedSymbol})` : `${name} (#${id}，無代號)`
}

// 將固定規則的提醒嚴重度映射到語意色彩。
function insightClass(severity: string): string {
  return severity === 'Warning'
    ? 'border-color-warning-border bg-color-warning-bg text-color-warning-text'
    : 'border-border-default bg-bg-raised text-text-secondary'
}

// 清除目前篩選並讓查詢回到全部持股範圍。
function clearFilters(): void {
  selectedBroker.value = ''
  selectedInstrumentType.value = ''
}

// 在持股結構分頁掛載時並行載入當前分析與全部持股趨勢。
function loadInitialData(): void {
  void structureQuery.refresh()
  void valueTrendQuery.refresh()
}

watch([selectedBroker, selectedInstrumentType], () => {
  void structureQuery.refresh()
})

watch(valueTrendMonths, () => {
  void valueTrendQuery.refresh()
})

onMounted(loadInitialData)
</script>

<template>
  <div class="space-y-6">
    <div class="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
      <div>
        <h2 class="text-base font-semibold text-text-primary">持股結構健檢</h2>
        <p class="mt-1 text-xs text-text-secondary">依目前持股與估算費稅計算，不代表實際投資績效或買賣建議。</p>
      </div>
      <div class="flex flex-col gap-2 sm:flex-row">
        <label class="text-xs text-text-secondary">
          <span class="sr-only">券商篩選</span>
          <select v-model="selectedBroker" data-testid="broker-filter" class="w-full rounded-lg border border-border-strong bg-bg-card px-3 py-2 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-focus-ring">
            <option value="">全部券商</option>
            <option v-for="broker in (structureData?.availableBrokers ?? [])" :key="broker" :value="broker">{{ broker }}</option>
          </select>
        </label>
        <label class="text-xs text-text-secondary">
          <span class="sr-only">商品類型篩選</span>
          <select v-model="selectedInstrumentType" data-testid="instrument-type-filter" class="w-full rounded-lg border border-border-strong bg-bg-card px-3 py-2 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-focus-ring">
            <option value="">全部商品類型</option>
            <option v-for="instrumentType in (structureData?.availableInstrumentTypes ?? [])" :key="instrumentType" :value="instrumentType">
              {{ formatStockInstrumentType(instrumentType) }}
            </option>
          </select>
        </label>
      </div>
    </div>

    <QueryState
      :status="structureQuery.status.value"
      :error-message="queryErrorMessage(structureQuery.error.value)"
      :empty-message="'沒有符合篩選的持股'"
      :last-success-at="structureQuery.lastSuccessAt.value"
      :retry="structureQuery.retry"
    >
      <div v-if="structureData" class="space-y-6">
        <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <Card>
            <p class="text-xs text-text-secondary">預估成本</p>
            <p class="mt-2 text-xl font-bold text-text-primary">{{ formatMoney(structureData.summary.totalEstimatedBuyCost) }}</p>
          </Card>
          <Card>
            <p class="text-xs text-text-secondary">預估賣出淨值</p>
            <p class="mt-2 text-xl font-bold text-text-primary">{{ formatMoney(structureData.summary.totalEstimatedNetSellValue) }}</p>
          </Card>
          <Card>
            <p class="text-xs text-text-secondary">預估未實現損益</p>
            <p class="mt-2 text-xl font-bold" :class="structureData.summary.totalEstimatedGainLoss >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">
              {{ formatMoney(structureData.summary.totalEstimatedGainLoss) }}
            </p>
          </Card>
          <Card>
            <p class="text-xs text-text-secondary">預估損益率</p>
            <p class="mt-2 text-xl font-bold" :class="structureData.summary.estimatedGainLossPercentage === null ? 'text-text-secondary' : structureData.summary.estimatedGainLossPercentage >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">
              {{ formatPercentage(structureData.summary.estimatedGainLossPercentage) }}
            </p>
          </Card>
        </div>

        <Card>
          <div class="flex items-center justify-between gap-3">
            <h3 class="text-sm font-semibold text-text-primary">結構提醒</h3>
            <span class="text-xs text-text-tertiary">固定門檻，僅描述目前資料</span>
          </div>
          <div class="mt-3 grid gap-2">
            <div v-for="insight in structureData.insights" :key="insight.code" class="rounded-lg border px-3 py-2 text-sm" :class="insightClass(insight.severity)">
              {{ insight.message }}
            </div>
          </div>
        </Card>

        <div v-if="canRenderAllocations" class="grid grid-cols-1 gap-6 xl:grid-cols-2">
          <Card>
            <h3 class="text-sm font-semibold text-text-primary">商品類型配置</h3>
            <div class="mt-3 h-[280px]">
              <Doughnut :data="instrumentChartData" :options="createDoughnutOptions(structureData.instrumentTypeAllocations)" />
            </div>
          </Card>
          <Card>
            <h3 class="text-sm font-semibold text-text-primary">標的集中度 Top 10</h3>
            <div class="mt-3 h-[280px]">
              <Bar :data="symbolChartData" :options="chartOptions" />
            </div>
          </Card>
          <Card>
            <h3 class="text-sm font-semibold text-text-primary">券商分布</h3>
            <div class="mt-3 h-[280px]">
              <Doughnut :data="brokerChartData" :options="createDoughnutOptions(structureData.brokerAllocations)" />
            </div>
          </Card>
          <Card>
            <h3 class="text-sm font-semibold text-text-primary">市場配置</h3>
            <div class="mt-3 h-[280px]">
              <Doughnut :data="marketChartData" :options="createDoughnutOptions(structureData.marketAllocations)" />
            </div>
          </Card>
        </div>
        <Card v-else>
          <p class="text-sm text-text-secondary">目前預估賣出淨值不大於 0，無法計算持股配置比例。</p>
        </Card>

        <div class="grid grid-cols-1 gap-6 xl:grid-cols-2">
          <Card>
            <div class="flex items-center justify-between gap-3">
              <h3 class="text-sm font-semibold text-text-primary">集中度</h3>
              <span class="text-xs text-text-tertiary">依預估賣出淨值計算</span>
            </div>
            <dl class="mt-3 grid grid-cols-2 gap-3 text-sm sm:grid-cols-5">
              <div><dt class="text-text-tertiary">Top 1</dt><dd class="mt-1 font-semibold text-text-primary">{{ formatAllocation(structureData.concentration.top1Percentage) }}</dd></div>
              <div><dt class="text-text-tertiary">Top 3</dt><dd class="mt-1 font-semibold text-text-primary">{{ formatAllocation(structureData.concentration.top3Percentage) }}</dd></div>
              <div><dt class="text-text-tertiary">Top 5</dt><dd class="mt-1 font-semibold text-text-primary">{{ formatAllocation(structureData.concentration.top5Percentage) }}</dd></div>
              <div><dt class="text-text-tertiary">HHI</dt><dd class="mt-1 font-semibold text-text-primary">{{ structureData.concentration.hhi?.toFixed(3) ?? '無法計算' }}</dd></div>
              <div><dt class="text-text-tertiary">有效持股數</dt><dd class="mt-1 font-semibold text-text-primary">{{ structureData.concentration.effectiveHoldingCount?.toFixed(1) ?? '無法計算' }}</dd></div>
            </dl>
            <div data-testid="concentration-insights" class="mt-3 space-y-1 text-xs text-text-secondary">
              <p>Top 5 涵蓋目前持股中前五大標的的預估賣出淨值占比。</p>
              <p>HHI 越接近 1，代表目前配置越集中；兩者僅描述目前資料。</p>
            </div>
          </Card>
          <Card
            data-testid="data-quality-warning"
            :title="`缺少更新與超過 ${structureData.dataQuality.staleAfterHours} 小時僅為資料新鮮度提示，不判定行情正確性。`"
            :class="structureData.dataQuality.missingLastPriceUpdateCount > 0 || structureData.dataQuality.stalePriceCount > 0 ? 'border-color-warning-border bg-color-warning-bg text-color-warning-text' : ''"
          >
            <div class="flex items-center justify-between gap-3">
              <h3 class="text-sm font-semibold text-text-primary">價格資料品質</h3>
              <span class="text-xs text-text-tertiary">{{ structureData.dataQuality.positivePriceCount }} / {{ structureData.dataQuality.holdingCount }} 正價格</span>
            </div>
            <div class="mt-3 grid grid-cols-2 gap-3 text-sm">
              <p class="text-text-secondary">價格覆蓋 <span class="font-semibold text-text-primary">{{ structureData.dataQuality.positivePriceCoverage === null ? '無法計算' : `${(structureData.dataQuality.positivePriceCoverage * 100).toFixed(1)}%` }}</span></p>
              <p class="text-text-secondary">缺少更新 <span class="font-semibold text-text-primary">{{ structureData.dataQuality.missingLastPriceUpdateCount }} 筆</span></p>
              <p class="text-text-secondary">最舊更新 <span class="font-semibold text-text-primary">{{ formatDataQualityTime(structureData.dataQuality.oldestLastPriceUpdateUtc) }}</span></p>
              <p class="text-text-secondary">最新更新 <span class="font-semibold text-text-primary">{{ formatDataQualityTime(structureData.dataQuality.latestLastPriceUpdateUtc) }}</span></p>
            </div>
            <p class="mt-3 text-xs" :class="structureData.dataQuality.missingLastPriceUpdateCount > 0 || structureData.dataQuality.stalePriceCount > 0 ? 'text-color-warning-text' : 'text-text-tertiary'">
              {{ structureData.dataQuality.stalePriceCount }} 筆超過 {{ structureData.dataQuality.staleAfterHours }} 小時；此為資料新鮮度提示，非行情正確性判定。
            </p>
          </Card>
        </div>

        <Card>
          <div class="flex flex-col gap-1 sm:flex-row sm:items-center sm:justify-between">
            <h3 class="text-sm font-semibold text-text-primary">持股明細</h3>
            <span class="text-xs text-text-tertiary">{{ structureData.summary.holdingCount }} 筆</span>
          </div>
          <div class="mt-4 overflow-x-auto">
            <table class="min-w-[900px] w-full text-sm">
              <thead>
                <tr class="border-b border-border-default">
                  <th class="py-2 text-left font-medium text-text-secondary">標的</th>
                  <th class="py-2 text-left font-medium text-text-secondary">類型</th>
                  <th class="py-2 text-right font-medium text-text-secondary">股數</th>
                  <th class="py-2 text-right font-medium text-text-secondary">成本</th>
                  <th class="py-2 text-right font-medium text-text-secondary">淨值</th>
                  <th class="py-2 text-right font-medium text-text-secondary">占比</th>
                  <th class="py-2 text-right font-medium text-text-secondary">損益</th>
                  <th class="py-2 text-left font-medium text-text-secondary">券商</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="holding in structureData.holdings" :key="holding.id" class="border-b border-border-default">
                  <td class="py-2 text-text-primary">{{ formatHoldingLabel(holding.name, holding.symbol, holding.id) }}</td>
                  <td class="py-2 text-text-secondary">{{ formatStockInstrumentType(holding.instrumentType) }}</td>
                  <td class="py-2 text-right text-text-primary">{{ formatShares(holding.shares) }}</td>
                  <td class="py-2 text-right text-text-primary">{{ formatMoney(holding.estimatedBuyCost) }}</td>
                  <td class="py-2 text-right text-text-primary">{{ formatMoney(holding.estimatedNetSellValue) }}</td>
                  <td class="py-2 text-right text-text-secondary">{{ formatAllocation(holding.allocationPercentage) }}</td>
                  <td class="py-2 text-right" :class="holding.estimatedGainLoss >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">{{ formatMoney(holding.estimatedGainLoss) }}</td>
                  <td class="py-2 text-text-secondary">{{ holding.broker?.trim() || '未指定券商' }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </Card>
      </div>
    </QueryState>
    <Card>
      <div class="flex flex-col gap-1 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h3 class="text-sm font-semibold text-text-primary">全部持股價值趨勢</h3>
          <p class="mt-1 text-xs text-text-tertiary">歷史快照資產價值，不等同投資報酬率；不受目前篩選影響</p>
        </div>
        <div class="flex rounded-lg bg-bg-raised p-1" role="group" aria-label="持股價值趨勢期間">
          <button
            v-for="months in [6, 12, 24, 36, 60] as const"
            :key="months"
            :data-testid="`value-trend-period-${months}`"
            type="button"
            class="rounded-md px-2 py-1 text-xs transition-colors cursor-pointer"
            :class="valueTrendMonths === months ? 'bg-bg-active text-text-primary shadow-sm' : 'text-text-secondary hover:text-text-primary'"
            @click="valueTrendMonths = months"
          >
            {{ months }}M
          </button>
        </div>
      </div>
      <QueryState
        :status="valueTrendQuery.status.value"
        :error-message="queryErrorMessage(valueTrendQuery.error.value)"
        :empty-message="'尚無全部持股價值歷史'"
        :last-success-at="valueTrendQuery.lastSuccessAt.value"
        :retry="valueTrendQuery.retry"
      >
        <div v-if="valueTrendData.length === 1" class="flex h-[280px] flex-col items-center justify-center gap-2 text-sm text-text-tertiary">
          <span>目前只有 1 筆全部持股價值快照</span>
          <span>尚不足以形成趨勢</span>
        </div>
        <div v-else class="h-[280px]">
          <Line :data="valueTrendChartData" :options="chartOptions" />
        </div>
      </QueryState>
    </Card>
    <button
      v-if="structureQuery.status.value === 'empty' && (selectedBroker || selectedInstrumentType)"
      type="button"
      class="text-sm text-accent-primary underline underline-offset-2"
      data-testid="clear-stock-structure-filters"
      @click="clearFilters"
    >
      清除篩選
    </button>
  </div>
</template>
