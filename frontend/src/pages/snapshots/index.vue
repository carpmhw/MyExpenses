<script setup lang="ts">
import { ref, computed, inject, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '../../api'
import type { SnapshotBatch, TrendPoint, AutoSnapshotConfig, SnapshotListResponse } from '../../types'
import Card from '../../components/ui/Card.vue'
import Button from '../../components/ui/Button.vue'
import DataTable from '../../components/ui/DataTable.vue'
import QueryState from '../../components/ui/QueryState.vue'
import Modal from '../../components/ui/Modal.vue'
import SnapshotDetailModal from '../../components/snapshots/SnapshotDetailModal.vue'
import ConfirmDialog from '../../components/ui/ConfirmDialog.vue'
import Icon from '../../components/ui/Icon.vue'
import { formatMoney } from '../../utils/format'
import { coerceSnapshotDateRange, createDefaultSnapshotDateRange, hasCompleteNetWorthBasis } from '../../utils/snapshot'
import { usePagination } from '../../composables/usePagination'
import { useTimeZone } from '../../composables/useTimeZone'
import { getSystemDateParts } from '../../utils/timezone'
import { getThemeColor } from '../../utils/themeColor'
import { Line } from 'vue-chartjs'
import { useAsyncQuery } from '../../composables/useAsyncQuery'
import { useAsyncMutation } from '../../composables/useAsyncMutation'
import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  PointElement,
  LineElement,
  Title,
  Tooltip,
  Legend,
  Filler,
} from 'chart.js'

ChartJS.register(CategoryScale, LinearScale, PointElement, LineElement, Title, Tooltip, Legend, Filler)

const router = useRouter()
const toast = inject<{ success: (m: string) => void; error: (m: string) => void }>('toast')!
const timeZone = useTimeZone()
const darkMode = inject<{ isDark: { value: boolean } }>('darkMode')!

const pagination = usePagination(1, 15)
const defaultSnapshotRange = createDefaultSnapshotDateRange(new Date(), timeZone.timeZoneId.value)
const dateStart = ref(defaultSnapshotRange.dateStart)
const dateEnd = ref(defaultSnapshotRange.dateEnd)

const selectedIds = ref<number[]>([])
const detailId = ref<number | null>(null)
const detailOpen = ref(false)

const confirmOpen = ref(false)
const deletingId = ref<number | null>(null)

const defaultSchedule: AutoSnapshotConfig = {
  id: 0,
  isEnabled: false,
  frequency: 'Daily',
  dayOfWeek: null,
  dayOfMonth: null,
  timeOfDay: '08:00',
  lastRunAt: null,
}
const scheduleOpen = ref(false)
const scheduleForm = ref<AutoSnapshotConfig>({ ...defaultSchedule })

const snapshotsQuery = useAsyncQuery<SnapshotListResponse>({
  key: () => ({
    resource: 'snapshots',
    page: pagination.page.value,
    pageSize: pagination.pageSize.value,
    dateStart: dateStart.value,
    dateEnd: dateEnd.value,
  }),
  query: ({ signal }) => api.snapshots.list({
    page: pagination.page.value,
    pageSize: pagination.pageSize.value,
    dateStart: dateStart.value,
    dateEnd: dateEnd.value,
  }, { signal }),
  isEmpty: result => result.items.length === 0,
})

const trendQuery = useAsyncQuery<TrendPoint[]>({
  key: () => ({ resource: 'snapshot-trend', dateStart: dateStart.value, dateEnd: dateEnd.value }),
  query: ({ signal }) => api.snapshots.trend({ dateStart: dateStart.value, dateEnd: dateEnd.value }, { signal }),
  isEmpty: data => data.length === 0,
})

const detailQuery = useAsyncQuery<SnapshotBatch>({
  key: () => ({ resource: 'snapshot-detail', id: detailId.value }),
  query: ({ signal }) => api.snapshots.get(detailId.value!, { signal }),
  immediate: false,
})

const scheduleQuery = useAsyncQuery<AutoSnapshotConfig>({
  key: () => ({ resource: 'snapshot-schedule' }),
  query: ({ signal }) => api.snapshots.getSchedule({ signal }),
  immediate: false,
})

const deleteMutation = useAsyncMutation<number, void>({
  mutate: (id, { signal }) => api.snapshots.delete(id, { signal }),
  onSuccess: async () => {
    if (deletingId.value !== null) selectedIds.value = selectedIds.value.filter(id => id !== deletingId.value)
    await Promise.all([snapshotsQuery.refresh(), trendQuery.refresh()])
  },
})

const scheduleMutation = useAsyncMutation<AutoSnapshotConfig, AutoSnapshotConfig>({
  mutate: (data, { signal }) => api.snapshots.updateSchedule(data, { signal }),
  onSuccess: data => {
    scheduleForm.value = { ...data }
    scheduleOpen.value = false
    toast.success('排程設定已儲存')
  },
})

const snapshots = computed(() => snapshotsQuery.data.value?.items ?? [])
const snapshotTotal = computed(() => snapshotsQuery.data.value?.total ?? 0)
const detailSnapshot = computed(() => detailQuery.data.value ?? null)
const trendData = computed(() => trendQuery.data.value ?? [])

const chartColors = computed(() => {
  const theme = darkMode.isDark.value ? 'dark' : 'light'
  return {
    text: getThemeColor('--color-text-secondary', theme === 'dark' ? '#B8C0CC' : '#4C566A'),
    primary: getThemeColor('--color-text-primary', theme === 'dark' ? '#ECEFF4' : '#2E3440'),
    grid: getThemeColor('--color-chart-grid', theme === 'dark' ? '#4B5563' : '#D2DAE4'),
    axis: getThemeColor('--color-chart-axis', theme === 'dark' ? '#8794A8' : '#758399'),
    surface: getThemeColor('--color-bg-card', theme === 'dark' ? '#3B4252' : '#F8FAFC'),
    income: getThemeColor('--color-color-income-chart', theme === 'dark' ? '#A3BE8C' : '#6F8F5E'),
    incomeSoft: getThemeColor('--color-color-income-chart-bg', theme === 'dark' ? 'rgb(163 190 140 / 14%)' : 'rgb(111 143 94 / 12%)'),
    info: getThemeColor('--color-color-info-chart', theme === 'dark' ? '#81A1C1' : '#4F759D'),
    infoSoft: getThemeColor('--color-color-info-chart-bg', theme === 'dark' ? 'rgb(129 161 193 / 14%)' : 'rgb(79 117 157 / 12%)'),
    warning: getThemeColor('--color-color-warning-chart', theme === 'dark' ? '#EBCB8B' : '#A56C26'),
    warningSoft: getThemeColor('--color-color-warning-chart-bg', theme === 'dark' ? 'rgb(235 203 139 / 14%)' : 'rgb(165 108 38 / 12%)'),
  }
})

const columns = [
  { key: 'select', label: '選取' },
  { key: 'seq', label: '序號' },
  { key: 'snapshotDate', label: '快照日期' },
  { key: 'name', label: '名稱' },
  { key: 'totalNetWorth', label: '資產/淨值', align: 'right' as const },
  { key: 'totalBankBalance', label: '銀行總額', align: 'right' as const },
  { key: 'totalStockValue', label: '股票預估賣出淨值', align: 'right' as const },
]

// Formats snapshot timestamps using the configured application time zone.
function formatDate(dateStr: string) {
  return timeZone.formatDateTime(dateStr)
}

// Formats the truthful aggregate for a legacy or complete snapshot row.
function formatSnapshotAggregate(snapshot: SnapshotBatch): string {
  return formatMoney(hasCompleteNetWorthBasis(snapshot) ? snapshot.totalNetWorth : snapshot.totalAssets)
}

// Labels the aggregate basis so legacy asset totals cannot be mistaken for net worth.
function snapshotBasisLabel(snapshot: SnapshotBatch): string {
  return hasCompleteNetWorthBasis(snapshot) ? '完整淨值' : '資產總額'
}

const canCompare = computed(() => selectedIds.value.length === 2)
const hasCompleteTrend = computed(() => trendData.value.some(hasCompleteNetWorthBasis))

// Maintains at most two selected snapshot IDs for comparison.
function toggleSelect(id: number) {
  const idx = selectedIds.value.indexOf(id)
  if (idx >= 0) {
    selectedIds.value.splice(idx, 1)
  } else {
    if (selectedIds.value.length >= 2) {
      selectedIds.value.shift()
    }
    selectedIds.value.push(id)
  }
}

// Navigates to comparison only when exactly two snapshots are selected.
function goCompare() {
  if (selectedIds.value.length !== 2) return
  const [id1, id2] = selectedIds.value
  router.push(`/snapshots/compare?ids=${id1},${id2}`)
}

const trendChartData = computed(() => ({
  labels: trendData.value.map(t => {
    const date = getSystemDateParts(t.date, timeZone.timeZoneId.value)
    return `${date.month}/${date.day}`
  }),
  datasets: [
    {
      label: '資產總額',
      data: trendData.value.map(t => t.totalAssets),
      borderColor: chartColors.value.income,
      backgroundColor: chartColors.value.incomeSoft,
      fill: true,
      tension: 0.3,
    },
    {
      label: '完整淨值',
      data: trendData.value.map(t => hasCompleteNetWorthBasis(t) ? t.totalNetWorth : null),
      borderColor: chartColors.value.warning,
      backgroundColor: 'transparent',
      fill: false,
      tension: 0.3,
    },
    {
      label: '銀行總額',
      data: trendData.value.map(t => t.totalBankBalance),
       borderColor: chartColors.value.info,
       backgroundColor: chartColors.value.infoSoft,
      fill: true,
      tension: 0.3,
    },
    {
      label: '股票預估賣出淨值',
      data: trendData.value.map(t => t.totalStockValue),
       borderColor: chartColors.value.warning,
       backgroundColor: chartColors.value.warningSoft,
      fill: true,
      tension: 0.3,
    },
  ],
}))

const trendChartOptions = computed(() => ({
  responsive: true,
  maintainAspectRatio: false,
  plugins: {
    legend: {
      position: 'bottom' as const,
      labels: { color: chartColors.value.text },
    },
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
      beginAtZero: true,
      ticks: {
        color: chartColors.value.text,
        callback: (value: string | number) => formatMoney(Number(value)),
      },
      grid: { color: chartColors.value.grid },
      border: { color: chartColors.value.axis },
    },
  },
}))

// Keeps the snapshot date range valid before it is used in list and trend requests.
function normalizeSnapshotDateRange() {
  const normalized = coerceSnapshotDateRange({ dateStart: dateStart.value, dateEnd: dateEnd.value })

  if (normalized.changed) {
    dateStart.value = normalized.dateStart
    dateEnd.value = normalized.dateEnd
    if (normalized.reason === 'range-too-long') {
      toast.error('日期區間最多只能查詢 5 年，已自動調整起日')
    } else {
      toast.error('迄日不能小於起日，已調整為起日')
    }
    return false
  }

  return true
}

// Resets pagination when the date range changes; query keys own the resulting reloads.
function refreshSnapshotsForDateRange() {
  if (!normalizeSnapshotDateRange()) return

  if (pagination.page.value !== 1) {
    pagination.page.value = 1
  }
}

// Loads the selected snapshot through its own detail query identity.
function showDetail(snapshot: SnapshotBatch) {
  detailId.value = snapshot.id
  detailOpen.value = true
  void detailQuery.refresh()
}

// Opens the confirmation dialog for one snapshot without changing query state.
function confirmDelete(id: number) {
  deletingId.value = id
  confirmOpen.value = true
}

// Submits deletion and keeps refresh failures separate from confirmed command success.
async function doDelete() {
  if (deletingId.value === null) return
  try {
    await deleteMutation.submit(deletingId.value)
    confirmOpen.value = false
    toast.success('快照已刪除')
    deletingId.value = null
  } catch (e) {
    toast.error(e instanceof Error ? e.message : '刪除失敗')
  }
}

// Opens the schedule dialog and loads its configuration through the schedule query.
async function openSchedule() {
  scheduleOpen.value = true
  await scheduleQuery.refresh()
  scheduleForm.value = { ...(scheduleQuery.data.value ?? defaultSchedule) }
}

// Submits schedule changes and leaves server-confirmed values as the canonical form state.
async function saveSchedule() {
  try {
    await scheduleMutation.submit({ ...scheduleForm.value })
  } catch (e) {
    toast.error(e instanceof Error ? e.message : '儲存失敗')
  }
}

watch(() => snapshotsQuery.data.value?.total, total => {
  pagination.total.value = total ?? 0
})
watch([dateStart, dateEnd], refreshSnapshotsForDateRange)
</script>

<template>
  <div class="p-4 lg:p-6">
    <div class="flex items-center justify-between mb-6">
      <div>
        <h1 class="text-2xl font-bold text-text-primary">財務快照</h1>
        <p class="text-xs text-text-secondary mt-1">資產歷史紀錄 · Snapshots</p>
      </div>
      <div class="flex items-center gap-2">
        <Button
          v-if="canCompare"
          @click="goCompare"
        >
          比對選取快照
        </Button>
        <button
          class="p-2 rounded-lg hover:bg-bg-raised text-text-secondary cursor-pointer transition-colors"
          title="自動排程設定"
          @click="openSchedule"
        >
          <Icon name="settings" :size="18" />
        </button>
      </div>
    </div>

    <Card class="mb-6">
      <div class="flex flex-col sm:flex-row sm:items-end gap-4">
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">起日</label>
          <input
            v-model="dateStart"
            type="date"
            class="w-full sm:w-44 px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
          />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">迄日</label>
          <input
            v-model="dateEnd"
            type="date"
            class="w-full sm:w-44 px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
          />
        </div>
        <p class="text-xs text-text-secondary sm:pb-2">預設顯示最近一年快照，列表與趨勢圖會套用相同區間。</p>
      </div>
    </Card>

    <Card class="mb-6">
      <div class="p-4">
        <QueryState
          :status="trendQuery.status.value"
          :error-message="trendQuery.error.value instanceof Error ? trendQuery.error.value.message : '載入趨勢失敗，請重試。'"
          :empty-message="'尚無快照資料，無法顯示趨勢圖'"
          :last-success-at="trendQuery.lastSuccessAt.value"
          :retry="trendQuery.retry"
        >
          <h2 class="text-sm font-semibold text-text-primary mb-3">資產/淨值趨勢</h2>
          <div class="h-64">
            <Line :data="trendChartData" :options="trendChartOptions" />
          </div>
          <p v-if="trendData.length > 0 && !hasCompleteTrend" class="mt-3 text-xs text-text-secondary">
             尚無完整淨值歷史，目前僅顯示資產總額
          </p>
        </QueryState>
      </div>
    </Card>

    <Card>
      <DataTable
        :columns="columns"
        :loading="snapshotsQuery.status.value === 'loading'"
        :items="snapshots"
        :error="snapshotsQuery.status.value === 'error' || snapshotsQuery.status.value === 'stale'
          ? (snapshotsQuery.error.value instanceof Error ? snapshotsQuery.error.value.message : '載入快照失敗，請重試。')
          : null"
        :refreshing="snapshotsQuery.status.value === 'refreshing'"
        :retry="snapshotsQuery.retry"
      >
        <template #empty>
          <div class="text-center text-text-tertiary py-4">尚無快照資料</div>
        </template>
        <tr v-for="(item, index) in snapshots" :key="item.id" class="border-b border-border-default hover:bg-bg-raised">
          <td class="py-3 px-4 w-[60px]">
            <input
              type="checkbox"
              :checked="selectedIds.includes(item.id)"
              class="w-4 h-4 rounded border-border-strong cursor-pointer"
              @change="toggleSelect(item.id)"
            />
          </td>
          <td class="py-3 px-4 text-text-secondary text-sm w-[60px]">{{ (pagination.page.value - 1) * pagination.pageSize.value + index + 1 }}</td>
          <td class="py-3 px-4 text-text-secondary w-[160px]">{{ formatDate(item.snapshotDate) }}</td>
          <td class="py-3 px-4 text-text-primary font-medium">{{ item.name }}</td>
           <td class="py-3 px-4 text-text-primary font-bold text-sm text-right">
             <div>{{ formatSnapshotAggregate(item) }}</div>
             <div class="text-[10px] font-normal text-text-secondary">{{ snapshotBasisLabel(item) }}</div>
           </td>
          <td class="py-3 px-4 text-text-secondary text-sm text-right">{{ formatMoney(item.totalBankBalance) }}</td>
          <td class="py-3 px-4 text-text-secondary text-sm text-right">{{ formatMoney(item.totalStockValue) }}</td>
          <td class="py-3 px-4 w-[120px]">
            <div class="flex items-center gap-1">
              <button
                class="p-1.5 rounded-lg hover:bg-bg-raised text-text-secondary cursor-pointer transition-colors"
                title="檢視明細"
                aria-label="檢視明細"
                @click="showDetail(item)"
              >
                <Icon name="eye" :size="16" />
              </button>
              <button
                class="p-1.5 rounded-lg hover:bg-bg-raised text-color-expense-text cursor-pointer transition-colors"
                title="刪除快照"
                aria-label="刪除快照"
                @click="confirmDelete(item.id)"
              >
                <Icon name="trash-2" :size="16" />
              </button>
            </div>
          </td>
        </tr>
      </DataTable>

      <div class="flex items-center justify-between px-4 py-3 border-t border-border-default">
        <span class="text-sm text-text-secondary">共 {{ snapshotTotal }} 筆</span>
        <div class="flex items-center gap-2">
          <Button variant="ghost" :disabled="!pagination.hasPrev.value" @click="pagination.prev()">上一頁</Button>
          <span class="text-sm text-text-secondary">{{ pagination.page.value }} / {{ pagination.totalPages.value }}</span>
          <Button variant="ghost" :disabled="!pagination.hasNext.value" @click="pagination.next()">下一頁</Button>
        </div>
      </div>
    </Card>

    <SnapshotDetailModal
      v-if="detailQuery.status.value === 'success'"
      v-model:open="detailOpen"
      :snapshot="detailSnapshot"
    />

    <Modal v-else-if="detailOpen" :open="detailOpen" title="快照明細" @update:open="detailOpen = $event">
      <QueryState
        :status="detailQuery.status.value"
        :error-message="detailQuery.error.value instanceof Error ? detailQuery.error.value.message : '載入明細失敗，請重試。'"
        :retry="detailQuery.retry"
      />
    </Modal>

    <Modal :open="scheduleOpen" title="自動排程設定" @update:open="scheduleOpen = $event">
      <QueryState
        v-if="scheduleQuery.status.value === 'loading' || scheduleQuery.status.value === 'error'"
        :status="scheduleQuery.status.value"
        :error-message="scheduleQuery.error.value instanceof Error ? scheduleQuery.error.value.message : '載入排程失敗，請重試。'"
        :retry="openSchedule"
      />
      <form v-else class="space-y-4" @submit.prevent="saveSchedule">
        <div class="flex items-center gap-3">
          <label class="text-sm font-medium text-text-primary">啟用自動排程</label>
          <input
            type="checkbox"
            :checked="scheduleForm.isEnabled"
            class="w-4 h-4 rounded border-border-strong cursor-pointer"
            @change="scheduleForm.isEnabled = !scheduleForm.isEnabled"
          />
        </div>

        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">頻率</label>
          <select
            v-model="scheduleForm.frequency"
            class="w-full px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
          >
            <option value="Daily">每日</option>
            <option value="Weekly">每週</option>
            <option value="Monthly">每月</option>
          </select>
        </div>

        <div v-if="scheduleForm.frequency === 'Weekly'">
          <label class="block text-sm font-medium text-text-primary mb-1">星期幾</label>
          <select
            :value="scheduleForm.dayOfWeek ?? 1"
            @change="scheduleForm.dayOfWeek = Number(($event.target as HTMLSelectElement).value)"
            class="w-full px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
          >
            <option :value="0">日</option>
            <option :value="1">一</option>
            <option :value="2">二</option>
            <option :value="3">三</option>
            <option :value="4">四</option>
            <option :value="5">五</option>
            <option :value="6">六</option>
          </select>
        </div>

        <div v-if="scheduleForm.frequency === 'Monthly'">
          <label class="block text-sm font-medium text-text-primary mb-1">日期（1-31）</label>
          <input
            :value="scheduleForm.dayOfMonth ?? 1"
            type="number"
            min="1"
            max="31"
            class="w-full px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
            @input="scheduleForm.dayOfMonth = Number(($event.target as HTMLInputElement).value) || 1"
          />
        </div>

        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">時間</label>
          <input
            v-model="scheduleForm.timeOfDay"
            type="time"
            class="w-full px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
          />
        </div>

        <div v-if="scheduleForm.lastRunAt" class="text-xs text-text-secondary">
          上次執行：{{ formatDate(scheduleForm.lastRunAt) }}
        </div>

        <div class="flex justify-end gap-3 pt-2">
          <Button variant="ghost" type="button" @click="scheduleOpen = false">取消</Button>
          <Button type="submit" :loading="scheduleMutation.status.value === 'submitting'">儲存</Button>
        </div>
      </form>
    </Modal>

    <ConfirmDialog
      :open="confirmOpen"
      title="刪除快照"
      description="確定要刪除此快照嗎？此操作無法復原。"
      variant="danger"
      @update:open="confirmOpen = $event"
      @confirm="doDelete"
    />
  </div>
</template>
