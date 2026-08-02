<script setup lang="ts">
import { ref, computed, inject, onMounted, watch } from 'vue'
import { api } from '../../api'
import type { MonthlyTrend, CategoryDistribution, NetWorth, MonthlyForecast, NetWorthTrendPoint } from '../../types'
import Card from '../../components/ui/Card.vue'
import Icon from '../../components/ui/Icon.vue'
import { formatMoney, formatShares } from '../../utils/format'
import { formatStockInstrumentType } from '../../utils/stock'
import { addCalendarDays, getCurrentYearRange } from '../../utils/timezone'
import { getThemeColor } from '../../utils/themeColor'
import { useTimeZone } from '../../composables/useTimeZone'
import { Bar, Line, Doughnut } from 'vue-chartjs'
import {
  Chart as ChartJS,
  CategoryScale, LinearScale, BarElement, PointElement, LineElement,
  ArcElement, Title, Tooltip, Legend, Filler
} from 'chart.js'

ChartJS.register(CategoryScale, LinearScale, BarElement, PointElement, LineElement, ArcElement, Title, Tooltip, Legend, Filler)

const toast = inject<{ error: (m: string) => void }>('toast')!
const timeZone = useTimeZone()

function getDefaultStartDate(): string {
  return getCurrentYearRange(new Date(), timeZone.timeZoneId.value).start
}
function getDefaultEndDate(): string {
  return getCurrentYearRange(new Date(), timeZone.timeZoneId.value).end
}

const activeTab = ref<'trend' | 'category' | 'networth' | 'forecast'>('trend')
const startDate = ref(getDefaultStartDate())
const endDate = ref(getDefaultEndDate())
const chartType = ref<'bar' | 'line'>('bar')
const selectedCategory = ref<CategoryDistribution | null>(null)

const trendData = ref<MonthlyTrend[]>([])
const categoryData = ref<CategoryDistribution[]>([])
const netWorthData = ref<NetWorth | null>(null)
const netWorthTrend = ref<NetWorthTrendPoint[]>([])
const forecastData = ref<MonthlyForecast[]>([])
const loading = ref(false)

function validateDateRange() {
  const s = startDate.value
  const e = endDate.value
  if (!s || !e) return
  const start = new Date(s)
  const end = new Date(e)
  if (end < start) {
    toast.error('迄日不能小於起日')
    endDate.value = startDate.value
    return
  }
  const diffDays = Math.ceil((end.getTime() - start.getTime()) / 86400000)
  if (diffDays > 365) {
    toast.error('日期區間不可超過 1 年')
    endDate.value = addCalendarDays(startDate.value, 365)
  }
}

const darkMode = inject<{ isDark: { value: boolean } }>('darkMode')!

const chartColors = computed(() => {
  const theme = darkMode.isDark.value ? 'dark' : 'light'
  return {
    text: getThemeColor('--color-text-secondary', theme === 'dark' ? '#B8C0CC' : '#4C566A'),
    primary: getThemeColor('--color-text-primary', theme === 'dark' ? '#ECEFF4' : '#2E3440'),
    grid: getThemeColor('--color-chart-grid', theme === 'dark' ? '#4B5563' : '#D2DAE4'),
    axis: getThemeColor('--color-chart-axis', theme === 'dark' ? '#8794A8' : '#758399'),
    surface: getThemeColor('--color-bg-card', theme === 'dark' ? '#3B4252' : '#F8FAFC'),
    income: getThemeColor('--color-color-income-chart', theme === 'dark' ? '#A3BE8C' : '#6F8F5E'),
    incomeChartBg: getThemeColor('--color-color-income-chart-bg', theme === 'dark' ? 'rgb(163 190 140 / 14%)' : 'rgb(111 143 94 / 12%)'),
    expense: getThemeColor('--color-color-expense', theme === 'dark' ? '#BF616A' : '#AA4F5A'),
    expenseChart: getThemeColor('--color-color-expense-chart', theme === 'dark' ? '#E6A5AB' : '#AA4F5A'),
    credit: getThemeColor('--color-color-credit', theme === 'dark' ? '#B48EAD' : '#8D6A88'),
    creditChart: getThemeColor('--color-color-credit-chart', theme === 'dark' ? '#B48EAD' : '#8D6A88'),
    info: getThemeColor('--color-color-info', theme === 'dark' ? '#81A1C1' : '#4F759D'),
  }
})

async function loadTrend() {
  try {
    trendData.value = await api.reports.incomeExpenseTrend({
      dateStart: startDate.value,
      dateEnd: endDate.value,
    })
  } catch {
    toast.error('載入收支趨勢失敗')
  }
}

async function loadCategory() {
  try {
    categoryData.value = await api.reports.categoryDistribution({
      dateStart: startDate.value,
      dateEnd: endDate.value,
    })
  } catch {
    toast.error('載入類別分布失敗')
  }
}

async function loadNetWorth() {
  try {
    netWorthData.value = await api.reports.netWorth()
  } catch {
    toast.error('載入資產負債失敗')
  }
}

// Loads only actual complete snapshot points for the historical net-worth chart.
async function loadNetWorthTrend() {
  try {
    netWorthTrend.value = await api.reports.netWorthTrend({ months: 6 })
  } catch {
    netWorthTrend.value = []
    toast.error('載入淨值歷史失敗')
  }
}

async function loadForecast() {
  try {
    forecastData.value = await api.reports.installmentForecast({ months: 6 })
  } catch {
    toast.error('載入分期預測失敗')
  }
}

async function loadAll() {
  loading.value = true
  await Promise.all([loadTrend(), loadCategory(), loadNetWorth(), loadNetWorthTrend(), loadForecast()])
  loading.value = false
}

onMounted(loadAll)

watch([startDate, endDate], () => {
  validateDateRange()
  loadTrend()
  loadCategory()
})

const trendChartData = computed(() => ({
  labels: trendData.value.map(d => d.month),
  datasets: [
    {
      label: '收入',
      backgroundColor: chartColors.value.income,
      borderColor: chartColors.value.income,
      borderWidth: 2,
      data: trendData.value.map(d => d.income),
      borderRadius: 4,
    },
    {
      label: '支出',
      backgroundColor: chartColors.value.expenseChart,
      borderColor: chartColors.value.expenseChart,
      borderWidth: 2,
      data: trendData.value.map(d => d.expense),
      borderRadius: 4,
    },
  ],
}))

const trendChartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  plugins: {
    legend: { labels: { color: chartColors.value.text } },
    tooltip: {
      backgroundColor: chartColors.value.surface,
      titleColor: chartColors.value.primary,
      bodyColor: chartColors.value.primary,
      borderColor: chartColors.value.grid,
      borderWidth: 1,
      callbacks: {
        label: (ctx: { dataset: { label?: string }; parsed: { y?: number | null } }) =>
          `${ctx.dataset.label ?? ''}: ${formatMoney(ctx.parsed.y ?? 0)}`,
      },
    },
  },
  scales: {
    x: {
      ticks: { color: chartColors.value.text },
      grid: { color: 'transparent' },
      border: { color: chartColors.value.axis },
    },
    y: {
      ticks: { color: chartColors.value.text, callback: (v: string | number) => formatMoney(Number(v)) },
      grid: { color: chartColors.value.grid },
      border: { color: chartColors.value.axis },
    },
  },
}))

const categoryChartData = computed(() => ({
  labels: categoryData.value.map(d => d.categoryName),
  datasets: [{
    data: categoryData.value.map(d => d.total),
    backgroundColor: categoryData.value.map(d => d.color || chartColors.value.info),
    borderWidth: 1,
    borderColor: chartColors.value.surface,
  }],
}))

const categoryChartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  plugins: {
    legend: {
      position: 'right' as const,
      labels: { color: chartColors.value.text, padding: 12 },
    },
    tooltip: {
      backgroundColor: chartColors.value.surface,
      titleColor: chartColors.value.primary,
      bodyColor: chartColors.value.primary,
      borderColor: chartColors.value.grid,
      borderWidth: 1,
      callbacks: {
        label: (ctx: { label: string; parsed: number }) => {
          const item = categoryData.value.find(d => d.categoryName === ctx.label)
          return `${ctx.label}: ${formatMoney(ctx.parsed)} (${item?.percentage.toFixed(1)}%)`
        },
      },
    },
  },
}))

const forecastChartData = computed(() => ({
  labels: forecastData.value.map(d => d.month),
  datasets: [{
    label: '預計應繳',
    backgroundColor: chartColors.value.creditChart,
    borderColor: chartColors.value.creditChart,
    borderWidth: 2,
    data: forecastData.value.map(d => d.totalAmount),
    borderRadius: 4,
  }],
}))

const forecastChartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  plugins: {
    legend: { labels: { color: chartColors.value.text } },
    tooltip: {
      backgroundColor: chartColors.value.surface,
      titleColor: chartColors.value.primary,
      bodyColor: chartColors.value.primary,
      borderColor: chartColors.value.grid,
      borderWidth: 1,
      callbacks: {
        label: (ctx: { parsed: { y?: number | null } }) => `預計應繳: ${formatMoney(ctx.parsed.y ?? 0)}`,
      },
    },
  },
  scales: {
    x: {
      ticks: { color: chartColors.value.text },
      grid: { color: 'transparent' },
      border: { color: chartColors.value.axis },
    },
    y: {
      ticks: { color: chartColors.value.text, callback: (v: string | number) => formatMoney(Number(v)) },
      grid: { color: chartColors.value.grid },
      border: { color: chartColors.value.axis },
    },
  },
}))

const netWorthTrendData = computed(() => {
  return {
    labels: netWorthTrend.value.map(point => point.month),
    datasets: [{
      label: '淨值',
      data: netWorthTrend.value.map(point => point.netWorth),
      borderColor: chartColors.value.income,
      backgroundColor: chartColors.value.incomeChartBg,
      fill: true,
      tension: 0.4,
      pointRadius: 4,
    }],
  }
})

const netWorthTrendOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  plugins: {
    legend: { labels: { color: chartColors.value.text } },
    tooltip: {
      backgroundColor: chartColors.value.surface,
      titleColor: chartColors.value.primary,
      bodyColor: chartColors.value.primary,
      borderColor: chartColors.value.grid,
      borderWidth: 1,
    },
  },
  scales: {
    x: {
      ticks: { color: chartColors.value.text },
      grid: { color: 'transparent' },
      border: { color: chartColors.value.axis },
    },
    y: {
      ticks: { color: chartColors.value.text, callback: (v: string | number) => formatMoney(Number(v)) },
      grid: { color: chartColors.value.grid },
      border: { color: chartColors.value.axis },
    },
  },
}))

function selectCategory(item: CategoryDistribution) {
  selectedCategory.value = selectedCategory.value?.categoryId === item.categoryId ? null : item
}
</script>

<template>
  <div class="space-y-6 p-6">
    <div class="flex items-center justify-between">
      <div>
        <h1 class="text-xl font-bold text-text-primary">報表分析</h1>
        <p class="text-sm text-text-secondary mt-0.5">財務數據視覺化 · Reports</p>
      </div>
      <div class="flex items-center gap-3">
        <div class="flex items-center gap-2">
          <input
            v-model="startDate"
            type="date"
            class="px-3 py-1.5 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring"
            @change="validateDateRange"
          />
          <span class="text-text-secondary text-sm">~</span>
          <input
            v-model="endDate"
            type="date"
            class="px-3 py-1.5 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring"
            @change="validateDateRange"
          />
        </div>
      </div>
    </div>

    <div class="flex gap-1 border-b border-border-default">
      <button
        v-for="tab in ([
          { key: 'trend', label: '收支趨勢' },
          { key: 'category', label: '類別分布' },
          { key: 'networth', label: '資產負債' },
          { key: 'forecast', label: '分期預測' },
        ] as const)"
        :key="tab.key"
        class="px-4 py-2.5 text-sm font-medium transition-colors border-b-2 -mb-px cursor-pointer"
        :class="activeTab === tab.key
          ? 'border-accent-primary text-accent-primary'
          : 'border-transparent text-text-secondary hover:text-text-primary'"
        @click="activeTab = tab.key"
      >
        {{ tab.label }}
      </button>
    </div>

    <div v-if="loading" class="flex items-center justify-center py-20">
      <Icon name="Loader2" :size="32" class="animate-spin text-text-secondary" />
    </div>

    <!-- 收支趨勢 -->
    <div v-else-if="activeTab === 'trend'">
      <Card>
        <div class="flex items-center justify-between mb-4">
          <h2 class="text-base font-semibold text-text-primary">每月收支趨勢</h2>
          <div class="flex gap-1 bg-bg-raised rounded-lg p-0.5">
            <button
              class="px-3 py-1 text-xs rounded-md transition-colors cursor-pointer"
              :class="chartType === 'bar' ? 'bg-bg-active text-text-primary shadow-sm' : 'text-text-secondary hover:text-text-primary'"
              @click="chartType = 'bar'"
            >長條圖</button>
            <button
              class="px-3 py-1 text-xs rounded-md transition-colors cursor-pointer"
              :class="chartType === 'line' ? 'bg-bg-active text-text-primary shadow-sm' : 'text-text-secondary hover:text-text-primary'"
              @click="chartType = 'line'"
            >折線圖</button>
          </div>
        </div>
        <div v-if="trendData.length === 0" class="text-center py-12 text-text-tertiary text-sm">暫無收支數據</div>
        <div v-else class="h-[360px]">
          <Bar v-if="chartType === 'bar'" :data="trendChartData" :options="trendChartOptions" />
          <Line v-else :data="trendChartData" :options="trendChartOptions" />
        </div>
      </Card>
    </div>

    <!-- 類別分布 -->
    <div v-else-if="activeTab === 'category'">
      <Card>
        <h2 class="text-base font-semibold text-text-primary mb-4">支出類別分布</h2>
        <div v-if="categoryData.length === 0" class="text-center py-12 text-text-tertiary text-sm">暫無支出數據</div>
        <div v-else class="flex gap-8">
          <div class="h-[360px] w-[400px] flex-shrink-0">
            <Doughnut :data="categoryChartData" :options="categoryChartOptions" />
          </div>
          <div class="flex-1 space-y-1.5 overflow-y-auto max-h-[360px]">
            <button
              v-for="item in categoryData"
              :key="item.categoryId"
              class="w-full flex items-center gap-3 px-3 py-2 rounded-lg hover:bg-bg-raised transition-colors text-left cursor-pointer"
              :class="selectedCategory?.categoryId === item.categoryId ? 'bg-bg-raised' : ''"
              @click="selectCategory(item)"
            >
              <span
                class="w-3 h-3 rounded-full flex-shrink-0"
                :style="{ backgroundColor: item.color || chartColors.info }"
              />
              <div class="flex-1 min-w-0">
                <div class="text-sm font-medium text-text-primary truncate">{{ item.categoryName }}</div>
                <div class="text-xs text-text-secondary">{{ item.percentage.toFixed(1) }}%</div>
              </div>
              <div class="text-sm font-medium text-text-primary">{{ formatMoney(item.total) }}</div>
            </button>
          </div>
        </div>
        <div v-if="selectedCategory" class="mt-4 pt-4 border-t border-border-default">
          <p class="text-sm font-medium text-text-primary mb-2">選取類別：{{ selectedCategory.categoryName }}</p>
          <p class="text-xs text-text-secondary">點擊其他類別切換，或再次點擊取消選取</p>
        </div>
      </Card>
    </div>

    <!-- 資產負債 -->
    <div v-else-if="activeTab === 'networth'">
      <div class="grid grid-cols-3 gap-4 mb-6">
        <Card>
          <p class="text-xs text-text-secondary mb-1">總資產</p>
          <p class="text-xl font-bold text-color-income-text">{{ formatMoney(netWorthData?.totalAssets ?? 0) }}</p>
        </Card>
        <Card>
          <p class="text-xs text-text-secondary mb-1">總負債</p>
          <p class="text-xl font-bold text-color-expense-text">{{ formatMoney(netWorthData?.totalLiabilities ?? 0) }}</p>
        </Card>
        <Card>
          <p class="text-xs text-text-secondary mb-1">淨值</p>
          <p class="text-xl font-bold" :class="(netWorthData?.netWorth ?? 0) >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">
            {{ formatMoney(netWorthData?.netWorth ?? 0) }}
          </p>
        </Card>
      </div>
      <div class="grid grid-cols-2 gap-6">
        <Card>
          <h3 class="text-sm font-semibold text-text-primary mb-3">銀行帳戶</h3>
          <table v-if="netWorthData?.bankAccounts.length" class="w-full text-sm">
            <thead>
              <tr class="border-b border-border-default">
                <th class="text-left py-2 text-text-secondary font-medium">銀行</th>
                <th class="text-left py-2 text-text-secondary font-medium">帳號</th>
                <th class="text-right py-2 text-text-secondary font-medium">餘額</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="acc in netWorthData.bankAccounts" :key="acc.accountNumber" class="border-b border-border-default">
                <td class="py-2 text-text-primary">{{ acc.bankName }}</td>
                <td class="py-2 text-text-secondary">{{ acc.accountNumber }}</td>
                <td class="py-2 text-right text-text-primary font-medium">{{ formatMoney(acc.balance) }}</td>
              </tr>
            </tbody>
          </table>
          <p v-else class="text-sm text-text-tertiary py-4 text-center">無銀行帳戶資料</p>
        </Card>
        <Card>
          <h3 class="text-sm font-semibold text-text-primary mb-3">股票持倉（預估賣出淨值）</h3>
          <table v-if="netWorthData?.stocks.length" class="w-full text-sm">
            <thead>
              <tr class="border-b border-border-default">
                <th class="text-left py-2 text-text-secondary font-medium">名稱</th>
                <th class="text-left py-2 text-text-secondary font-medium">類型</th>
                <th class="text-right py-2 text-text-secondary font-medium">股數</th>
                <th class="text-right py-2 text-text-secondary font-medium">現價</th>
                <th class="text-right py-2 text-text-secondary font-medium">預估賣出淨值</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="s in netWorthData.stocks" :key="s.symbol" class="border-b border-border-default">
                <td class="py-2 text-text-primary">{{ s.name }} ({{ s.symbol }})</td>
                <td class="py-2 text-text-secondary whitespace-nowrap">{{ formatStockInstrumentType(s.instrumentType) }}</td>
                <td class="py-2 text-right text-text-primary">{{ formatShares(s.shares) }}</td>
                <td class="py-2 text-right text-text-primary">{{ formatMoney(s.currentPrice) }}</td>
                <td class="py-2 text-right text-text-primary font-medium">{{ formatMoney(s.estimatedNetSellValue) }}</td>
              </tr>
            </tbody>
          </table>
          <p v-else class="text-sm text-text-tertiary py-4 text-center">無股票資料</p>
        </Card>
      </div>
      <Card class="mt-6">
        <h3 class="text-sm font-semibold text-text-primary mb-4">近 6 個月淨值趨勢</h3>
        <div v-if="netWorthTrend.length === 0" class="h-[300px] flex items-center justify-center text-text-tertiary text-sm">
          尚無完整淨值歷史
        </div>
        <div v-else-if="netWorthTrend.length === 1" class="h-[300px] flex flex-col items-center justify-center gap-2 text-text-tertiary text-sm">
          <span>目前只有 1 筆完整快照</span>
          <span>尚不足以形成趨勢</span>
        </div>
        <div v-else class="h-[300px]">
          <Line :data="netWorthTrendData" :options="netWorthTrendOptions" />
        </div>
      </Card>
    </div>

    <!-- 分期預測 -->
    <div v-else-if="activeTab === 'forecast'">
      <Card>
        <h2 class="text-base font-semibold text-text-primary mb-4">未來 6 個月分期應繳預測</h2>
        <div v-if="forecastData.length === 0 || forecastData.every(f => f.totalAmount === 0)" class="text-center py-12 text-text-tertiary text-sm">暫無分期預測數據</div>
        <template v-else>
          <div class="h-[360px]">
            <Bar :data="forecastChartData" :options="forecastChartOptions" />
          </div>
          <div v-for="month in forecastData.filter(f => f.totalAmount > 0)" :key="month.month" class="mt-6 pt-4 border-t border-border-default">
            <h3 class="text-sm font-semibold text-text-primary mb-2">{{ month.month }} - 共 {{ formatMoney(month.totalAmount) }}</h3>
            <table class="w-full text-sm">
              <thead>
                <tr class="border-b border-border-default">
                  <th class="text-left py-2 text-text-secondary font-medium">信用卡</th>
                  <th class="text-left py-2 text-text-secondary font-medium">描述</th>
                  <th class="text-center py-2 text-text-secondary font-medium">期數</th>
                  <th class="text-right py-2 text-text-secondary font-medium">金額</th>
                  <th class="text-right py-2 text-text-secondary font-medium">到期日</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="p in month.payments" :key="`${p.cardBankName}-${p.period}`" class="border-b border-border-default">
                  <td class="py-2 text-text-primary">{{ p.cardBankName }}</td>
                  <td class="py-2 text-text-secondary">{{ p.description }}</td>
                  <td class="py-2 text-center text-text-primary">第 {{ p.period }} 期</td>
                  <td class="py-2 text-right text-text-primary font-medium">{{ formatMoney(p.amount) }}</td>
                  <td class="py-2 text-right text-text-primary">{{ p.dueDate }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </template>
      </Card>
    </div>
  </div>
</template>
