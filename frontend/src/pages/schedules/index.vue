<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '../../api'
import type {
  ScheduleExecutionHistoryResponse,
  ScheduleOverviewItem,
  ScheduledJobExecutionStatus,
  ScheduledJobKey,
} from '../../types'
import Card from '../../components/ui/Card.vue'
import Button from '../../components/ui/Button.vue'
import Icon from '../../components/ui/Icon.vue'
import QueryState from '../../components/ui/QueryState.vue'
import { useAsyncQuery } from '../../composables/useAsyncQuery'
import { usePagination } from '../../composables/usePagination'
import { useTimeZone } from '../../composables/useTimeZone'

const router = useRouter()
const timeZone = useTimeZone()
const pagination = usePagination(1, 20)
const jobFilter = ref<ScheduledJobKey | ''>('')
const statusFilter = ref<ScheduledJobExecutionStatus | ''>('')
const dateStart = ref('')
const dateEnd = ref('')
const isVisible = ref(typeof document === 'undefined' || document.visibilityState !== 'hidden')
let pollTimer: ReturnType<typeof setInterval> | null = null

const overviewQuery = useAsyncQuery<ScheduleOverviewItem[]>({
  key: () => ({ resource: 'schedule-overview' }),
  query: ({ signal }) => api.schedules.overview({ signal }),
  isEmpty: value => value.length === 0,
})

const historyQuery = useAsyncQuery<ScheduleExecutionHistoryResponse>({
  key: () => ({
    resource: 'schedule-executions',
    jobKey: jobFilter.value,
    status: statusFilter.value,
    dateStart: dateStart.value,
    dateEnd: dateEnd.value,
    page: pagination.page.value,
    pageSize: pagination.pageSize.value,
  }),
  query: ({ signal }) => api.schedules.executions({
    jobKey: jobFilter.value,
    status: statusFilter.value,
    dateStart: dateStart.value,
    dateEnd: dateEnd.value,
    page: pagination.page.value,
    pageSize: pagination.pageSize.value,
  }, { signal }),
  isEmpty: value => value.items.length === 0,
})

const overviewItems = computed(() => overviewQuery.data.value ?? [])
const historyItems = computed(() => historyQuery.data.value?.items ?? [])
const statusOptions: ScheduledJobExecutionStatus[] = [
  'Running',
  'Succeeded',
  'PartiallySucceeded',
  'Failed',
  'Canceled',
  'Interrupted',
]

const statusLabels: Record<ScheduledJobExecutionStatus, string> = {
  Running: '執行中',
  Succeeded: '成功',
  PartiallySucceeded: '部分成功',
  Failed: '失敗',
  Canceled: '已取消',
  Interrupted: '已中斷',
}

const statusIcons: Record<ScheduledJobExecutionStatus, string> = {
  Running: 'clock',
  Succeeded: 'check-circle',
  PartiallySucceeded: 'alert-triangle',
  Failed: 'x-circle',
  Canceled: 'x-circle',
  Interrupted: 'alert-triangle',
}

const statusClasses: Record<ScheduledJobExecutionStatus, string> = {
  Running: 'text-color-info-text bg-color-info-bg',
  Succeeded: 'text-color-income-text bg-color-income-bg',
  PartiallySucceeded: 'text-color-warning-text bg-color-warning-bg',
  Failed: 'text-color-expense-text bg-color-expense-bg',
  Canceled: 'text-text-secondary bg-bg-raised',
  Interrupted: 'text-color-warning-text bg-color-warning-bg',
}

const isRefreshing = computed(() => overviewQuery.isInFlight.value || historyQuery.isInFlight.value)

// 以系統時區格式化後端 UTC timestamp。
function formatTimestamp(value: string | null): string {
  return value ? timeZone.formatDateTime(value) : '尚未執行'
}

// 將排程 status 同時呈現文字、圖示與顏色。
function formatStatus(status: ScheduledJobExecutionStatus | null): string {
  return status ? statusLabels[status] : '尚未執行'
}

// 以防禦方式取得 status 的視覺 class。
function getStatusClass(status: ScheduledJobExecutionStatus | null): string {
  return status ? statusClasses[status] : 'text-text-secondary bg-bg-raised'
}

// 以防禦方式取得 status 的 registered Lucide icon。
function getStatusIcon(status: ScheduledJobExecutionStatus | null): string {
  return status ? statusIcons[status] : 'clock'
}

// 將同一排程執行結果的數量組成安全摘要。
function formatCounts(item: ScheduleOverviewItem): string {
  const execution = item.latestExecution
  if (!execution) return '尚無執行紀錄'
  return `${execution.succeededCount} 成功 · ${execution.failedCount} 失敗 · ${execution.affectedCount} 受影響`
}

// 將 history filter 變更時分頁重設至第一頁。
function resetHistoryPage(): void {
  if (pagination.page.value !== 1) pagination.page.value = 1
}

// 清除目前輪詢 timer。
function stopPolling(): void {
  if (pollTimer !== null) {
    clearInterval(pollTimer)
    pollTimer = null
  }
}

// 只在頁面可見且同一 query 沒有 request 時觸發自動更新。
function refreshVisibleQueries(): void {
  if (!isVisible.value) return
  if (!overviewQuery.isInFlight.value) void overviewQuery.refresh()
  if (!historyQuery.isInFlight.value) void historyQuery.refresh()
}

// 啟動可見頁面的 30 秒 bounded polling。
function startPolling(): void {
  stopPolling()
  if (!isVisible.value) return
  refreshVisibleQueries()
  pollTimer = setInterval(refreshVisibleQueries, 30_000)
}

// 在 hidden 時取消 request，在 visible 時失效舊 response 並立即重取資料。
function handleVisibilityChange(): void {
  isVisible.value = document.visibilityState !== 'hidden'
  if (!isVisible.value) {
    stopPolling()
    overviewQuery.cancel()
    historyQuery.cancel()
    return
  }

  overviewQuery.cancel()
  historyQuery.cancel()
  startPolling()
}

// 提供手動重新整理並沿用 useAsyncQuery 的 latest-response 保護。
function refreshAll(): void {
  if (!isVisible.value) return
  void overviewQuery.refresh()
  void historyQuery.refresh()
}

// 將快照排程卡片導向既有設定頁，不在監控頁編輯固定排程。
function openSnapshotSettings(): void {
  void router.push('/snapshots')
}

watch([jobFilter, statusFilter, dateStart, dateEnd], resetHistoryPage)
watch(() => historyQuery.data.value?.total, total => {
  pagination.total.value = total ?? 0
})

onMounted(() => {
  document.addEventListener('visibilitychange', handleVisibilityChange)
  startPolling()
})

onUnmounted(() => {
  stopPolling()
  document.removeEventListener('visibilitychange', handleVisibilityChange)
  overviewQuery.dispose()
  historyQuery.dispose()
})
</script>

<template>
  <div class="min-h-full p-4 lg:p-6">
    <header class="mb-6 flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between">
      <div>
        <p class="text-xs font-semibold uppercase tracking-[0.18em] text-accent-primary">Operations / Rhythm</p>
        <h1 class="mt-1 text-2xl font-bold text-text-primary">業務排程</h1>
        <p class="mt-1 text-sm text-text-secondary">掌握三個業務工作最近一次執行與安全結果摘要。</p>
      </div>
      <Button variant="ghost" :loading="isRefreshing" @click="refreshAll">
        <Icon name="refresh-cw" :size="16" />
        立即更新
      </Button>
    </header>

    <QueryState
      :status="overviewQuery.status.value"
      :error-message="overviewQuery.error.value instanceof Error ? overviewQuery.error.value.message : '排程總覽載入失敗，請重試。'"
      :retry="overviewQuery.retry"
      :last-success-at="overviewQuery.lastSuccessAt.value"
    >
      <section class="mb-6 grid grid-cols-1 gap-4 md:grid-cols-2 xl:grid-cols-3" aria-label="排程總覽">
        <Card v-for="item in overviewItems" :key="item.jobKey" class="flex min-h-[220px] flex-col justify-between">
          <div>
            <div class="flex items-start justify-between gap-3">
              <div>
                <p class="text-xs font-semibold uppercase tracking-wider text-text-tertiary">{{ item.jobKey }}</p>
                <h2 class="mt-1 text-lg font-semibold text-text-primary">{{ item.displayName }}</h2>
              </div>
              <span
                class="inline-flex items-center gap-1 rounded-full px-2.5 py-1 text-xs font-medium"
                :class="getStatusClass(item.latestExecution?.status ?? null)"
              >
                <Icon :name="getStatusIcon(item.latestExecution?.status ?? null)" :size="14" />
                {{ formatStatus(item.latestExecution?.status ?? null) }}
              </span>
            </div>
            <dl class="mt-5 grid grid-cols-2 gap-x-4 gap-y-3 text-sm">
              <div>
                <dt class="text-xs text-text-tertiary">規則</dt>
                <dd class="mt-1 text-text-primary">{{ item.frequencyDescription }}</dd>
              </div>
              <div>
                <dt class="text-xs text-text-tertiary">時區</dt>
                <dd class="mt-1 truncate text-text-primary" :title="item.scheduleTimeZoneId">{{ item.scheduleTimeZoneId }}</dd>
              </div>
              <div>
                <dt class="text-xs text-text-tertiary">設定來源</dt>
                <dd class="mt-1 text-text-secondary">{{ item.configurationSource }}</dd>
              </div>
              <div>
                <dt class="text-xs text-text-tertiary">下次執行</dt>
                <dd class="mt-1 text-text-primary">{{ formatTimestamp(item.nextRunAtUtc) }}</dd>
              </div>
            </dl>
          </div>
          <div class="mt-5 flex items-end justify-between gap-3 border-t border-border-default pt-4">
            <div>
              <p class="text-xs text-text-tertiary">最近摘要</p>
              <p class="mt-1 text-xs text-text-secondary">{{ formatCounts(item) }}</p>
              <p v-if="item.latestExecution" class="mt-1 text-xs text-text-tertiary">
                {{ formatTimestamp(item.latestExecution.completedAtUtc ?? item.latestExecution.startedAtUtc) }}
              </p>
            </div>
            <Button v-if="item.jobKey === 'AutomaticSnapshot'" variant="ghost" @click="openSnapshotSettings">
              快照設定
            </Button>
          </div>
        </Card>
      </section>
    </QueryState>

    <Card>
      <div class="mb-5 flex flex-col gap-3 border-b border-border-default pb-5 lg:flex-row lg:items-end lg:justify-between">
        <div>
          <h2 class="text-lg font-semibold text-text-primary">執行歷史</h2>
          <p class="mt-1 text-xs text-text-secondary">依系統時區日期篩選，保留最近 90 天的安全摘要。</p>
        </div>
        <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <label class="text-xs text-text-secondary">
            排程
            <select v-model="jobFilter" class="mt-1 w-full rounded-lg border border-border-strong bg-bg-card px-3 py-2 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-focus-ring">
              <option value="">全部排程</option>
              <option value="AutomaticSnapshot">自動財務快照</option>
              <option value="StockPriceUpdate">目前股價更新</option>
              <option value="HistoricalMarketDataSync">歷史行情同步</option>
            </select>
          </label>
          <label class="text-xs text-text-secondary">
            狀態
            <select v-model="statusFilter" class="mt-1 w-full rounded-lg border border-border-strong bg-bg-card px-3 py-2 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-focus-ring">
              <option value="">全部狀態</option>
              <option v-for="status in statusOptions" :key="status" :value="status">{{ statusLabels[status] }}</option>
            </select>
          </label>
          <label class="text-xs text-text-secondary">
            起日
            <input v-model="dateStart" type="date" class="mt-1 w-full rounded-lg border border-border-strong bg-bg-card px-3 py-2 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-focus-ring" />
          </label>
          <label class="text-xs text-text-secondary">
            迄日
            <input v-model="dateEnd" type="date" class="mt-1 w-full rounded-lg border border-border-strong bg-bg-card px-3 py-2 text-sm text-text-primary focus:outline-none focus:ring-2 focus:ring-focus-ring" />
          </label>
        </div>
      </div>

      <QueryState
        :status="historyQuery.status.value"
        :error-message="historyQuery.error.value instanceof Error ? historyQuery.error.value.message : '執行歷史載入失敗，請重試。'"
        :retry="historyQuery.retry"
        :last-success-at="historyQuery.lastSuccessAt.value"
        empty-message="目前沒有符合條件的執行紀錄"
      >
        <div class="hidden overflow-hidden rounded-xl border border-border-default md:block">
          <table class="w-full text-left text-sm">
            <thead class="bg-bg-raised text-xs uppercase tracking-wider text-text-tertiary">
              <tr>
                <th class="px-4 py-3">排程</th>
                <th class="px-4 py-3">排定時間</th>
                <th class="px-4 py-3">狀態</th>
                <th class="px-4 py-3">Attempt</th>
                <th class="px-4 py-3">結果摘要</th>
                <th class="px-4 py-3">完成時間</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="item in historyItems" :key="item.id" class="border-t border-border-default text-text-primary">
                <td class="px-4 py-3">
                  <p class="font-medium">{{ item.jobKey }}</p>
                  <p class="mt-1 text-xs text-text-tertiary">{{ item.scheduledLocalDate }} · {{ item.scheduleTimeZoneId }}</p>
                </td>
                <td class="px-4 py-3 text-text-secondary">{{ formatTimestamp(item.scheduledForUtc) }}</td>
                <td class="px-4 py-3">
                  <span class="inline-flex items-center gap-1 rounded-full px-2 py-1 text-xs font-medium" :class="getStatusClass(item.status)">
                    <Icon :name="getStatusIcon(item.status)" :size="14" />
                    {{ formatStatus(item.status) }}
                  </span>
                </td>
                <td class="px-4 py-3 text-text-secondary">{{ item.attemptCount }}</td>
                <td class="max-w-[280px] px-4 py-3">
                  <p class="font-medium text-text-primary">{{ item.resultCode ?? '尚無結果代碼' }}</p>
                  <p class="mt-1 truncate text-xs text-text-secondary" :title="item.safeMessage ?? undefined">{{ item.safeMessage ?? '尚無安全訊息' }}</p>
                  <p class="mt-1 text-xs text-text-tertiary">{{ item.succeededCount }} 成功 · {{ item.failedCount }} 失敗 · {{ item.affectedCount }} 受影響</p>
                </td>
                <td class="px-4 py-3 text-text-secondary">{{ formatTimestamp(item.completedAtUtc) }}</td>
              </tr>
            </tbody>
          </table>
        </div>

        <div class="grid gap-3 md:hidden">
          <article v-for="item in historyItems" :key="item.id" class="rounded-xl border border-border-default bg-bg-raised p-4">
            <div class="flex items-start justify-between gap-3">
              <div>
                <p class="font-medium text-text-primary">{{ item.jobKey }}</p>
                <p class="mt-1 text-xs text-text-tertiary">{{ formatTimestamp(item.scheduledForUtc) }}</p>
              </div>
              <span class="inline-flex items-center gap-1 rounded-full px-2 py-1 text-xs font-medium" :class="getStatusClass(item.status)">
                <Icon :name="getStatusIcon(item.status)" :size="14" />
                {{ formatStatus(item.status) }}
              </span>
            </div>
            <dl class="mt-4 grid grid-cols-2 gap-3 text-xs">
              <div><dt class="text-text-tertiary">Attempt</dt><dd class="mt-1 text-text-primary">{{ item.attemptCount }}</dd></div>
              <div><dt class="text-text-tertiary">完成</dt><dd class="mt-1 text-text-primary">{{ formatTimestamp(item.completedAtUtc) }}</dd></div>
              <div class="col-span-2"><dt class="text-text-tertiary">結果</dt><dd class="mt-1 text-text-primary">{{ item.resultCode ?? '尚無結果代碼' }}</dd></div>
              <div class="col-span-2"><dt class="text-text-tertiary">摘要</dt><dd class="mt-1 text-text-secondary">{{ item.safeMessage ?? '尚無安全訊息' }}</dd></div>
              <div class="col-span-2 text-text-tertiary">{{ item.succeededCount }} 成功 · {{ item.failedCount }} 失敗 · {{ item.affectedCount }} 受影響</div>
            </dl>
          </article>
        </div>
      </QueryState>

      <div class="mt-5 flex flex-col gap-3 border-t border-border-default pt-4 sm:flex-row sm:items-center sm:justify-between">
        <span class="text-sm text-text-secondary">共 {{ pagination.total.value }} 筆</span>
        <div class="flex items-center gap-2">
          <Button variant="ghost" :disabled="!pagination.hasPrev.value" @click="pagination.prev()">上一頁</Button>
          <span class="text-sm text-text-secondary">{{ pagination.page.value }} / {{ pagination.totalPages.value }}</span>
          <Button variant="ghost" :disabled="!pagination.hasNext.value" @click="pagination.next()">下一頁</Button>
        </div>
      </div>
    </Card>
  </div>
</template>
