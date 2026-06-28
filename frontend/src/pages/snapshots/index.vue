<script setup lang="ts">
import { ref, computed, inject, watch, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '../../api'
import type { SnapshotBatch, TrendPoint, AutoSnapshotConfig } from '../../types'
import Card from '../../components/ui/Card.vue'
import Button from '../../components/ui/Button.vue'
import DataTable from '../../components/ui/DataTable.vue'
import Modal from '../../components/ui/Modal.vue'
import ConfirmDialog from '../../components/ui/ConfirmDialog.vue'
import Icon from '../../components/ui/Icon.vue'
import { formatMoney } from '../../utils/format'
import { coerceSnapshotDateRange, createDefaultSnapshotDateRange } from '../../utils/snapshot'
import { usePagination } from '../../composables/usePagination'
import { Line } from 'vue-chartjs'
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

const snapshots = ref<SnapshotBatch[]>([])
const loading = ref(false)
const pagination = usePagination(1, 15)
const defaultSnapshotRange = createDefaultSnapshotDateRange()
const dateStart = ref(defaultSnapshotRange.dateStart)
const dateEnd = ref(defaultSnapshotRange.dateEnd)

const selectedIds = ref<number[]>([])
const detailSnapshot = ref<SnapshotBatch | null>(null)
const detailOpen = ref(false)

const confirmOpen = ref(false)
const deletingId = ref<number | null>(null)

const trendData = ref<TrendPoint[]>([])

const scheduleOpen = ref(false)
const scheduleLoading = ref(false)
const scheduleSaving = ref(false)
const scheduleForm = ref<AutoSnapshotConfig>({
  id: 0,
  isEnabled: false,
  frequency: 'Daily',
  dayOfWeek: null,
  dayOfMonth: null,
  timeOfDay: '08:00',
  lastRunAt: null,
})

const columns = [
  { key: 'select', label: '選取' },
  { key: 'seq', label: '序號' },
  { key: 'snapshotDate', label: '快照日期' },
  { key: 'name', label: '名稱' },
  { key: 'totalNetWorth', label: '總淨值', align: 'right' as const },
  { key: 'totalBankBalance', label: '銀行總額', align: 'right' as const },
  { key: 'totalStockValue', label: '股票預估賣出淨值', align: 'right' as const },
]

function formatDate(dateStr: string) {
  const d = new Date(dateStr)
  return `${d.getFullYear()}/${String(d.getMonth() + 1).padStart(2, '0')}/${String(d.getDate()).padStart(2, '0')} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

const canCompare = computed(() => selectedIds.value.length === 2)

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

function goCompare() {
  if (selectedIds.value.length !== 2) return
  const [id1, id2] = selectedIds.value
  router.push(`/snapshots/compare?ids=${id1},${id2}`)
}

const trendChartData = computed(() => ({
  labels: trendData.value.map(t => {
    const d = new Date(t.date)
    return `${d.getMonth() + 1}/${d.getDate()}`
  }),
  datasets: [
    {
      label: '總淨值',
      data: trendData.value.map(t => t.totalNetWorth),
      borderColor: '#10B981',
      backgroundColor: 'rgba(16, 185, 129, 0.1)',
      fill: true,
      tension: 0.3,
    },
    {
      label: '銀行總額',
      data: trendData.value.map(t => t.totalBankBalance),
      borderColor: '#3B82F6',
      backgroundColor: 'rgba(59, 130, 246, 0.1)',
      fill: true,
      tension: 0.3,
    },
    {
      label: '股票預估賣出淨值',
      data: trendData.value.map(t => t.totalStockValue),
      borderColor: '#F59E0B',
      backgroundColor: 'rgba(245, 158, 11, 0.1)',
      fill: true,
      tension: 0.3,
    },
  ],
}))

const trendChartOptions = {
  responsive: true,
  maintainAspectRatio: false,
  plugins: {
    legend: {
      position: 'bottom' as const,
    },
  },
  scales: {
    y: {
      beginAtZero: true,
      ticks: {
        callback: (value: string | number) => formatMoney(Number(value)),
      },
    },
  },
}

async function fetchList() {
  loading.value = true
  try {
    const result = await api.snapshots.list({
      page: pagination.page.value,
      pageSize: pagination.pageSize.value,
      dateStart: dateStart.value,
      dateEnd: dateEnd.value,
    })
    snapshots.value = result.items
    pagination.total.value = result.total
  } finally {
    loading.value = false
  }
}

async function fetchTrend() {
  try {
    trendData.value = await api.snapshots.trend({ dateStart: dateStart.value, dateEnd: dateEnd.value })
  } catch {
    // trend data is optional
  }
}

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

// Reloads snapshot list and trend data using the current shared date range.
async function refreshSnapshotsForDateRange() {
  if (!normalizeSnapshotDateRange()) return

  if (pagination.page.value !== 1) {
    pagination.page.value = 1
  } else {
    await fetchList()
  }
  await fetchTrend()
}

function showDetail(snapshot: SnapshotBatch) {
  detailSnapshot.value = snapshot
  detailOpen.value = true
}

function confirmDelete(id: number) {
  deletingId.value = id
  confirmOpen.value = true
}

async function doDelete() {
  if (deletingId.value !== null) {
    try {
      await api.snapshots.delete(deletingId.value)
      confirmOpen.value = false
      deletingId.value = null
      toast.success('快照已刪除')
      selectedIds.value = selectedIds.value.filter(id => id !== deletingId.value)
      await fetchList()
      await fetchTrend()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : '刪除失敗')
    }
  }
}

async function openSchedule() {
  scheduleLoading.value = true
  scheduleOpen.value = true
  try {
    const config = await api.snapshots.getSchedule()
    scheduleForm.value = { ...config }
  } catch {
    scheduleForm.value = {
      id: 0,
      isEnabled: false,
      frequency: 'Daily',
      dayOfWeek: null,
      dayOfMonth: null,
      timeOfDay: '08:00',
      lastRunAt: null,
    }
  } finally {
    scheduleLoading.value = false
  }
}

async function saveSchedule() {
  scheduleSaving.value = true
  try {
    await api.snapshots.updateSchedule(scheduleForm.value)
    toast.success('排程設定已儲存')
    scheduleOpen.value = false
  } catch (e) {
    toast.error(e instanceof Error ? e.message : '儲存失敗')
  } finally {
    scheduleSaving.value = false
  }
}

onMounted(() => {
  fetchList()
  fetchTrend()
})

watch(() => pagination.page.value, () => fetchList())
watch([dateStart, dateEnd], () => refreshSnapshotsForDateRange())
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
          class="p-2 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 text-text-secondary cursor-pointer transition-colors"
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
            class="w-full sm:w-44 px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary"
          />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">迄日</label>
          <input
            v-model="dateEnd"
            type="date"
            class="w-full sm:w-44 px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary"
          />
        </div>
        <p class="text-xs text-text-secondary sm:pb-2">預設顯示最近一年快照，列表與趨勢圖會套用相同區間。</p>
      </div>
    </Card>

    <Card class="mb-6">
      <div class="p-4">
        <h2 class="text-sm font-semibold text-text-primary mb-3">淨值趨勢</h2>
        <div class="h-64" v-if="trendData.length > 0">
          <Line :data="trendChartData" :options="trendChartOptions" />
        </div>
        <div v-else class="h-64 flex items-center justify-center text-text-tertiary text-sm">
          尚無快照資料，無法顯示趨勢圖
        </div>
      </div>
    </Card>

    <Card>
      <DataTable :columns="columns" :loading="loading" :items="snapshots">
        <template #empty>
          <div class="text-center text-text-tertiary py-4">尚無快照資料</div>
        </template>
        <tr v-for="(item, index) in snapshots" :key="item.id" class="border-b border-border-default hover:bg-gray-100 dark:hover:bg-gray-700">
          <td class="py-3 px-4 w-[60px]">
            <input
              type="checkbox"
              :checked="selectedIds.includes(item.id)"
              class="w-4 h-4 rounded border-gray-300 cursor-pointer"
              @change="toggleSelect(item.id)"
            />
          </td>
          <td class="py-3 px-4 text-text-secondary text-sm w-[60px]">{{ (pagination.page.value - 1) * pagination.pageSize.value + index + 1 }}</td>
          <td class="py-3 px-4 text-text-secondary w-[160px]">{{ formatDate(item.snapshotDate) }}</td>
          <td class="py-3 px-4 text-text-primary font-medium">{{ item.name }}</td>
          <td class="py-3 px-4 text-text-primary font-bold text-sm text-right">{{ formatMoney(item.totalNetWorth) }}</td>
          <td class="py-3 px-4 text-text-secondary text-sm text-right">{{ formatMoney(item.totalBankBalance) }}</td>
          <td class="py-3 px-4 text-text-secondary text-sm text-right">{{ formatMoney(item.totalStockValue) }}</td>
          <td class="py-3 px-4 w-[120px]">
            <div class="flex items-center gap-1">
              <button
                class="p-1.5 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 text-text-secondary cursor-pointer transition-colors"
                title="檢視明細"
                @click="showDetail(item)"
              >
                <Icon name="eye" :size="16" />
              </button>
              <button
                class="p-1.5 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 text-red-500 cursor-pointer transition-colors"
                title="刪除快照"
                @click="confirmDelete(item.id)"
              >
                <Icon name="trash-2" :size="16" />
              </button>
            </div>
          </td>
        </tr>
      </DataTable>

      <div class="flex items-center justify-between px-4 py-3 border-t border-border-default">
        <span class="text-sm text-text-secondary">共 {{ pagination.total.value }} 筆</span>
        <div class="flex items-center gap-2">
          <Button variant="ghost" :disabled="!pagination.hasPrev.value" @click="pagination.prev()">上一頁</Button>
          <span class="text-sm text-text-secondary">{{ pagination.page.value }} / {{ pagination.totalPages.value }}</span>
          <Button variant="ghost" :disabled="!pagination.hasNext.value" @click="pagination.next()">下一頁</Button>
        </div>
      </div>
    </Card>

    <Modal :open="detailOpen" title="快照明細" @update:open="detailOpen = $event">
      <div v-if="detailSnapshot" class="space-y-6">
        <div class="grid grid-cols-2 gap-4">
          <div>
            <p class="text-xs text-text-secondary">快照名稱</p>
            <p class="text-sm font-medium text-text-primary">{{ detailSnapshot.name }}</p>
          </div>
          <div>
            <p class="text-xs text-text-secondary">快照日期</p>
            <p class="text-sm font-medium text-text-primary">{{ formatDate(detailSnapshot.snapshotDate) }}</p>
          </div>
        </div>

        <div class="grid grid-cols-2 sm:grid-cols-4 gap-4">
          <div class="bg-gray-50 dark:bg-gray-800 rounded-lg p-3">
            <p class="text-xs text-text-secondary">總淨值</p>
            <p class="text-lg font-bold text-emerald-600">{{ formatMoney(detailSnapshot.totalNetWorth) }}</p>
          </div>
          <div class="bg-gray-50 dark:bg-gray-800 rounded-lg p-3">
            <p class="text-xs text-text-secondary">銀行總額</p>
            <p class="text-lg font-bold text-blue-600">{{ formatMoney(detailSnapshot.totalBankBalance) }}</p>
          </div>
          <div class="bg-gray-50 dark:bg-gray-800 rounded-lg p-3">
            <p class="text-xs text-text-secondary">股票預估賣出淨值</p>
            <p class="text-lg font-bold text-amber-600">{{ formatMoney(detailSnapshot.totalStockValue) }}</p>
          </div>
          <div class="bg-gray-50 dark:bg-gray-800 rounded-lg p-3">
            <p class="text-xs text-text-secondary">股票估算成本</p>
            <p class="text-lg font-bold text-text-primary">{{ formatMoney(detailSnapshot.totalStockCost) }}</p>
          </div>
        </div>

        <div v-if="detailSnapshot.bankDetails.length > 0">
          <h3 class="text-sm font-semibold text-text-primary mb-2">銀行明細</h3>
          <div class="overflow-x-auto">
            <table class="w-full text-sm">
              <thead>
                <tr class="border-b border-border-default">
                  <th class="text-left py-2 px-2 text-text-secondary font-medium">銀行</th>
                  <th class="text-left py-2 px-2 text-text-secondary font-medium">帳號</th>
                  <th class="text-right py-2 px-2 text-text-secondary font-medium">餘額</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="b in detailSnapshot.bankDetails" :key="b.accountNumber" class="border-b border-border-default">
                  <td class="py-2 px-2 text-text-primary">{{ b.bankName }}</td>
                  <td class="py-2 px-2 text-text-secondary font-mono">{{ b.accountNumber }}</td>
                  <td class="py-2 px-2 text-text-primary text-right">{{ formatMoney(b.balance) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>

        <div v-if="detailSnapshot.stockDetails.length > 0">
          <h3 class="text-sm font-semibold text-text-primary mb-2">股票明細</h3>
          <div class="overflow-x-auto">
            <table class="w-full text-sm">
              <thead>
                <tr class="border-b border-border-default">
                  <th class="text-left py-2 px-2 text-text-secondary font-medium">名稱</th>
                  <th class="text-left py-2 px-2 text-text-secondary font-medium">代號</th>
                  <th class="text-right py-2 px-2 text-text-secondary font-medium">股數</th>
                  <th class="text-right py-2 px-2 text-text-secondary font-medium">預估賣出淨值</th>
                  <th class="text-right py-2 px-2 text-text-secondary font-medium">預估損益</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="s in detailSnapshot.stockDetails" :key="s.symbol" class="border-b border-border-default">
                  <td class="py-2 px-2 text-text-primary">{{ s.name }}</td>
                  <td class="py-2 px-2 text-text-secondary font-mono">{{ s.symbol }}</td>
                  <td class="py-2 px-2 text-text-primary text-right">{{ s.shares }}</td>
                  <td class="py-2 px-2 text-text-primary text-right">{{ formatMoney(s.marketValue) }}</td>
                  <td class="py-2 px-2 text-right" :class="s.gainLoss >= 0 ? 'text-green-600' : 'text-red-600'">
                    {{ formatMoney(s.gainLoss) }}
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
      </div>
    </Modal>

    <Modal :open="scheduleOpen" title="自動排程設定" @update:open="scheduleOpen = $event">
      <div v-if="scheduleLoading" class="text-center py-4 text-text-tertiary">載入中...</div>
      <form v-else class="space-y-4" @submit.prevent="saveSchedule">
        <div class="flex items-center gap-3">
          <label class="text-sm font-medium text-text-primary">啟用自動排程</label>
          <input
            type="checkbox"
            :checked="scheduleForm.isEnabled"
            class="w-4 h-4 rounded border-gray-300 cursor-pointer"
            @change="scheduleForm.isEnabled = !scheduleForm.isEnabled"
          />
        </div>

        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">頻率</label>
          <select
            v-model="scheduleForm.frequency"
            class="w-full px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary"
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
            class="w-full px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary"
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
            class="w-full px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary"
            @input="scheduleForm.dayOfMonth = Number(($event.target as HTMLInputElement).value) || 1"
          />
        </div>

        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">時間</label>
          <input
            v-model="scheduleForm.timeOfDay"
            type="time"
            class="w-full px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary"
          />
        </div>

        <div v-if="scheduleForm.lastRunAt" class="text-xs text-text-secondary">
          上次執行：{{ formatDate(scheduleForm.lastRunAt) }}
        </div>

        <div class="flex justify-end gap-3 pt-2">
          <Button variant="ghost" type="button" @click="scheduleOpen = false">取消</Button>
          <Button type="submit" :loading="scheduleSaving">儲存</Button>
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
