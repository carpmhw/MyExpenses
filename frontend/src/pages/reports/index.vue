<script setup lang="ts">
import { ref, computed, inject, onMounted, watch } from 'vue'
import { api } from '../../api'
import type { MonthlyTrend, CategoryDistribution, NetWorth, MonthlyForecast } from '../../types'
import Card from '../../components/ui/Card.vue'
import Icon from '../../components/ui/Icon.vue'
import { formatMoney, formatShares } from '../../utils/format'
import { formatStockInstrumentType } from '../../utils/stock'
import { Bar, Line, Doughnut } from 'vue-chartjs'
import {
  Chart as ChartJS,
  CategoryScale, LinearScale, BarElement, PointElement, LineElement,
  ArcElement, Title, Tooltip, Legend, Filler
} from 'chart.js'

ChartJS.register(CategoryScale, LinearScale, BarElement, PointElement, LineElement, ArcElement, Title, Tooltip, Legend, Filler)

const toast = inject<{ error: (m: string) => void }>('toast')!

function getDefaultStartDate(): string {
  const d = new Date()
  return `${d.getFullYear()}-01-01`
}
function getDefaultEndDate(): string {
  const d = new Date()
  return `${d.getFullYear()}-12-31`
}

const activeTab = ref<'trend' | 'category' | 'networth' | 'forecast'>('trend')
const startDate = ref(getDefaultStartDate())
const endDate = ref(getDefaultEndDate())
const chartType = ref<'bar' | 'line'>('bar')
const selectedCategory = ref<CategoryDistribution | null>(null)

const trendData = ref<MonthlyTrend[]>([])
const categoryData = ref<CategoryDistribution[]>([])
const netWorthData = ref<NetWorth | null>(null)
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
    const maxEnd = new Date(start)
    maxEnd.setDate(maxEnd.getDate() + 365)
    endDate.value = maxEnd.toISOString().slice(0, 10)
  }
}

const darkMode = inject<{ isDark: { value: boolean } }>('darkMode')!

const chartTextColor = computed(() => darkMode.isDark.value ? '#E2E8F0' : '#334155')

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

async function loadForecast() {
  try {
    forecastData.value = await api.reports.installmentForecast({ months: 6 })
  } catch {
    toast.error('載入分期預測失敗')
  }
}

async function loadAll() {
  loading.value = true
  await Promise.all([loadTrend(), loadCategory(), loadNetWorth(), loadForecast()])
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
      backgroundColor: '#10B981',
      borderColor: '#10B981',
      borderWidth: 2,
      data: trendData.value.map(d => d.income),
      borderRadius: 4,
    },
    {
      label: '支出',
      backgroundColor: '#EF4444',
      borderColor: '#EF4444',
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
    legend: { labels: { color: chartTextColor.value } },
    tooltip: {
      callbacks: {
        label: (ctx: { dataset: { label?: string }; parsed: { y?: number | null } }) =>
          `${ctx.dataset.label ?? ''}: ${formatMoney(ctx.parsed.y ?? 0)}`,
      },
    },
  },
  scales: {
    x: { ticks: { color: chartTextColor.value }, grid: { color: 'transparent' } },
    y: {
      ticks: { color: chartTextColor.value, callback: (v: string | number) => formatMoney(Number(v)) },
      grid: { color: darkMode.isDark.value ? '#1E293B' : '#E2E8F0' },
    },
  },
}))

const categoryChartData = computed(() => ({
  labels: categoryData.value.map(d => d.categoryName),
  datasets: [{
    data: categoryData.value.map(d => d.total),
    backgroundColor: categoryData.value.map(d => d.color || '#94A3B8'),
    borderWidth: 1,
    borderColor: darkMode.isDark.value ? '#1E293B' : '#FFFFFF',
  }],
}))

const categoryChartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  plugins: {
    legend: {
      position: 'right' as const,
      labels: { color: chartTextColor.value, padding: 12 },
    },
    tooltip: {
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
    backgroundColor: '#8B5CF6',
    borderColor: '#8B5CF6',
    borderWidth: 2,
    data: forecastData.value.map(d => d.totalAmount),
    borderRadius: 4,
  }],
}))

const forecastChartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  plugins: {
    legend: { labels: { color: chartTextColor.value } },
    tooltip: {
      callbacks: {
        label: (ctx: { parsed: { y?: number | null } }) => `預計應繳: ${formatMoney(ctx.parsed.y ?? 0)}`,
      },
    },
  },
  scales: {
    x: { ticks: { color: chartTextColor.value }, grid: { color: 'transparent' } },
    y: {
      ticks: { color: chartTextColor.value, callback: (v: string | number) => formatMoney(Number(v)) },
      grid: { color: darkMode.isDark.value ? '#1E293B' : '#E2E8F0' },
    },
  },
}))

const netWorthTrendLabels = computed(() => {
  const labels: string[] = []
  for (let i = 5; i >= 0; i--) {
    const d = new Date()
    d.setMonth(d.getMonth() - i)
    labels.push(`${d.getFullYear()}/${String(d.getMonth() + 1).padStart(2, '0')}`)
  }
  return labels
})

const netWorthTrendData = computed(() => {
  const current = netWorthData.value?.netWorth ?? 0
  const step = current / 6
  return {
    labels: netWorthTrendLabels.value,
    datasets: [{
      label: '淨值',
      data: netWorthTrendLabels.value.map((_, i) => step * (i + 1)),
      borderColor: '#10B981',
      backgroundColor: 'rgba(16, 185, 129, 0.1)',
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
    legend: { labels: { color: chartTextColor.value } },
  },
  scales: {
    x: { ticks: { color: chartTextColor.value }, grid: { color: 'transparent' } },
    y: {
      ticks: { color: chartTextColor.value, callback: (v: string | number) => formatMoney(Number(v)) },
      grid: { color: darkMode.isDark.value ? '#1E293B' : '#E2E8F0' },
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
            class="px-3 py-1.5 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30"
            @change="validateDateRange"
          />
          <span class="text-text-secondary text-sm">~</span>
          <input
            v-model="endDate"
            type="date"
            class="px-3 py-1.5 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30"
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
          <div class="flex gap-1 bg-gray-100 dark:bg-gray-700 rounded-lg p-0.5">
            <button
              class="px-3 py-1 text-xs rounded-md transition-colors cursor-pointer"
              :class="chartType === 'bar' ? 'bg-white dark:bg-gray-600 text-text-primary shadow-sm' : 'text-text-secondary hover:text-text-primary'"
              @click="chartType = 'bar'"
            >長條圖</button>
            <button
              class="px-3 py-1 text-xs rounded-md transition-colors cursor-pointer"
              :class="chartType === 'line' ? 'bg-white dark:bg-gray-600 text-text-primary shadow-sm' : 'text-text-secondary hover:text-text-primary'"
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
              class="w-full flex items-center gap-3 px-3 py-2 rounded-lg hover:bg-gray-50 dark:hover:bg-gray-800 transition-colors text-left cursor-pointer"
              :class="selectedCategory?.categoryId === item.categoryId ? 'bg-gray-50 dark:bg-gray-800' : ''"
              @click="selectCategory(item)"
            >
              <span
                class="w-3 h-3 rounded-full flex-shrink-0"
                :style="{ backgroundColor: item.color || '#94A3B8' }"
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
          <p class="text-xl font-bold text-green-500">{{ formatMoney(netWorthData?.totalAssets ?? 0) }}</p>
        </Card>
        <Card>
          <p class="text-xs text-text-secondary mb-1">總負債</p>
          <p class="text-xl font-bold text-red-500">{{ formatMoney(netWorthData?.totalLiabilities ?? 0) }}</p>
        </Card>
        <Card>
          <p class="text-xs text-text-secondary mb-1">淨值</p>
          <p class="text-xl font-bold" :class="(netWorthData?.netWorth ?? 0) >= 0 ? 'text-green-500' : 'text-red-500'">
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
        <div class="h-[300px]">
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
