<script setup lang="ts">
import { ref, computed } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError, api } from '../../api'
import type { Withdrawal, Transaction, Installment, DashboardSummary } from '../../types'
import Icon from '../../components/ui/Icon.vue'
import QueryState from '../../components/ui/QueryState.vue'
import { formatCurrency, formatMoney } from '../../utils/format'
import { formatDateOnly, getSystemDateParts } from '../../utils/timezone'
import { useTimeZone } from '../../composables/useTimeZone'
import { useAsyncQuery } from '../../composables/useAsyncQuery'

const router = useRouter()
const timeZone = useTimeZone()
const initialSystemDate = getSystemDateParts(new Date(), timeZone.timeZoneId.value)

const year = ref(initialSystemDate.year)
const month = ref(initialSystemDate.month)

// 回傳 Dashboard 選定月份的第一個日曆日。
function getMonthStart(y: number, m: number): string {
  return `${y}-${String(m).padStart(2, '0')}-01`
}
// 回傳 Dashboard 選定月份的最後一個日曆日。
function getMonthEnd(y: number, m: number): string {
  const d = new Date(y, m, 0)
  return `${y}-${String(m).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}
// 計算 Dashboard 前一月份並正確處理跨年邊界。
function prevMonthKey(y: number, m: number): { year: number; month: number } {
  return m === 1 ? { year: y - 1, month: 12 } : { year: y, month: m - 1 }
}

const startDate = computed(() => getMonthStart(year.value, month.value))
const endDate = computed(() => getMonthEnd(year.value, month.value))

// 將 Dashboard 期間往前移動一個月。
function goPrev() {
  const k = prevMonthKey(year.value, month.value)
  year.value = k.year; month.value = k.month
}
// 將 Dashboard 期間往後移動一個月。
function goNext() {
  if (month.value === 12) { year.value++; month.value = 1 }
  else month.value++
}
// 將 Dashboard 期間還原為目前系統月份。
function goCurrent() {
  const current = getSystemDateParts(new Date(), timeZone.timeZoneId.value)
  year.value = current.year
  month.value = current.month
}
const isCurrentMonth = computed(() =>
  year.value === getSystemDateParts(new Date(), timeZone.timeZoneId.value).year
  && month.value === getSystemDateParts(new Date(), timeZone.timeZoneId.value).month
)

const summaryQuery = useAsyncQuery<DashboardSummary>({
  key: () => ['dashboard-summary', year.value, month.value],
  query: ({ signal }) => api.reports.dashboardSummary({ year: year.value, month: month.value }, { signal }),
})
const withdrawalsQuery = useAsyncQuery<{ items: Withdrawal[] }>({
  key: () => ['dashboard-withdrawals', year.value, month.value],
  query: ({ signal }) => api.withdrawals.list({ page: 1, startDate: startDate.value, endDate: endDate.value, pageSize: 50 }, { signal }),
  isEmpty: result => result.items.length === 0,
})
const expensesQuery = useAsyncQuery<{ items: Transaction[] }>({
  key: () => ['dashboard-expenses', year.value, month.value],
  query: ({ signal }) => api.transactions.list({ page: 1, startDate: startDate.value, endDate: endDate.value, type: 'Expense', pageSize: 50 }, { signal }),
  isEmpty: result => result.items.length === 0,
})
const installmentsQuery = useAsyncQuery<{ items: Installment[] }>({
  key: () => ['dashboard-installments', year.value, month.value],
  query: ({ signal }) => api.installments.list({ page: 1, status: 'Active', pageSize: 50 }, { signal }),
  isEmpty: result => result.items.length === 0,
})

const dashboardSummary = computed(() => summaryQuery.data.value ?? null)
const withdrawals = computed(() => withdrawalsQuery.data.value?.items ?? [])
const expenses = computed(() => expensesQuery.data.value?.items ?? [])
const activeInstallments = computed(() => installmentsQuery.data.value?.items ?? [])
const loading = computed(() => [
  summaryQuery.status.value,
  withdrawalsQuery.status.value,
  expensesQuery.status.value,
  installmentsQuery.status.value,
].some(status => status === 'loading'))
const hasAnyData = computed(() => Boolean(
  dashboardSummary.value
  || withdrawalsQuery.data.value
  || expensesQuery.data.value
  || installmentsQuery.data.value,
))

// 將 typed query error 轉成不暴露原始 response 的安全行內訊息。
function queryErrorMessage(error: unknown): string {
  return error instanceof ApiError ? error.userMessage : '載入失敗，請重試。'
}

// 依 Dashboard response 的基準幣別格式化 nullable summary 金額。
function formatSummaryAmount(amount: number | null): string {
  return amount === null
    ? '不可用'
    : formatCurrency(amount, dashboardSummary.value?.baseCurrency ?? 'TWD')
}

const totalWithdrawals = computed(() =>
  dashboardSummary.value?.totalWithdrawals ?? null
)
const totalExpenses = computed(() =>
  dashboardSummary.value?.totalExpenses ?? null
)
const disposableBalance = computed(() => dashboardSummary.value?.disposableBalance ?? null)

const prevDisposable = computed(() =>
  dashboardSummary.value?.previousDisposableBalance ?? null
)
const comparisonPct = computed(() => {
  if (prevDisposable.value === null || disposableBalance.value === null || prevDisposable.value === 0) return null
  return ((disposableBalance.value - prevDisposable.value) / Math.abs(prevDisposable.value) * 100)
})

const installmentMonthlyDue = computed(() =>
  dashboardSummary.value?.installmentDueAmount ?? null
)

const recentWithdrawals = computed(() =>
  [...withdrawals.value]
    .sort((a, b) => new Date(b.date).getTime() - new Date(a.date).getTime())
    .slice(0, 5)
)
const recentExpenses = computed(() =>
  [...expenses.value]
    .sort((a, b) => new Date(b.date).getTime() - new Date(a.date).getTime())
    .slice(0, 5)
)
const recentInstallments = computed(() =>
  [...activeInstallments.value]
    .sort((a, b) => {
      const da = a.transaction?.date ?? a.createdAt
      const db = b.transaction?.date ?? b.createdAt
      return new Date(db).getTime() - new Date(da).getTime()
    })
    .slice(0, 4)
)

// 格式化分期資料列顯示的已繳期數進度。
function progressLabel(i: Installment): string {
  const paid = i.periods - i.remainingPeriods
  return `${paid}/${i.periods}`
}

// 將 date-only 值格式化為 Dashboard 月日標籤。
function formatDateMMDD(d: string): string {
  const formatted = formatDateOnly(d)
  return formatted.includes('/') ? formatted.slice(5) : d
}

// 依設定的系統時區將事件時間格式化為月日標籤。
function formatEventDateMMDD(timestamp: string): string {
  return timeZone.formatDateTime(timestamp).slice(5)
}
</script>

<template>
  <div class="p-4 sm:p-6 space-y-6">
    <!-- Header -->
    <div class="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
      <div class="space-y-1">
        <div class="flex items-center gap-2">
          <h1 class="text-xl font-bold text-text-primary">財務總覽</h1>
          <div class="flex items-center gap-1.5 bg-bg-card border border-border-default rounded-md px-2.5 py-1">
            <Icon name="Calendar" :size="13" class="text-text-secondary" />
            <span class="text-xs text-text-secondary">{{ year }} 年 {{ month }} 月</span>
          </div>
        </div>
        <p class="text-sm text-text-secondary">追蹤您的提款、支出與信用卡分期，隨時掌握財務狀態。</p>
      </div>
      <div class="flex items-center gap-2">
        <div class="flex items-center gap-1 bg-bg-card border border-border-subtle rounded-lg px-3 py-2">
          <button
            aria-label="上一個月"
            class="p-0.5 text-text-secondary hover:text-text-primary cursor-pointer disabled:opacity-30 disabled:cursor-not-allowed"
            :disabled="loading"
            @click="goPrev"
          >
            <Icon name="ChevronLeft" :size="16" />
          </button>
          <button
            aria-label="回到當月"
            class="text-xs font-medium text-text-primary px-2 cursor-pointer disabled:opacity-50"
            :disabled="isCurrentMonth"
            @click="goCurrent"
          >
            {{ year }}/{{ String(month).padStart(2, '0') }}
          </button>
          <button
            aria-label="下一個月"
            class="p-0.5 text-text-secondary hover:text-text-primary cursor-pointer disabled:opacity-30 disabled:cursor-not-allowed"
            :disabled="isCurrentMonth || loading"
            @click="goNext"
          >
            <Icon name="ChevronRight" :size="16" />
          </button>
        </div>
        <button
          class="flex items-center gap-1.5 bg-bg-card border border-border-subtle rounded-lg px-3.5 py-2 text-xs font-medium text-text-primary hover:bg-bg-raised cursor-pointer"
          @click="router.push('/reports')"
        >
          <Icon name="ChartColumn" :size="15" class="text-text-secondary" />
          查看報表
        </button>
      </div>
    </div>

    <div v-if="loading && !hasAnyData" class="flex items-center justify-center py-32" role="status" aria-live="polite">
      <Icon name="Loader2" :size="32" class="animate-spin text-text-secondary" />
    </div>

    <template v-else>
      <!-- Hero Card -->
      <QueryState
        :status="summaryQuery.status.value"
        :error-message="queryErrorMessage(summaryQuery.error.value)"
        :last-success-at="summaryQuery.lastSuccessAt.value"
        :retry="summaryQuery.retry"
      >
        <div
          class="flex flex-col rounded-2xl overflow-hidden md:flex-row"
          style="background: linear-gradient(135deg, var(--color-bg-hero-start), var(--color-bg-hero-mid) 50%, var(--color-bg-hero-end))"
        >
        <div data-testid="dashboard-hero-summary" class="flex-1 flex flex-col justify-between p-7 gap-3">
          <div class="space-y-3">
            <div class="inline-flex items-center gap-1.5 bg-color-income-hero-bg rounded-full px-3 py-1">
              <span class="w-1.5 h-1.5 rounded-full bg-color-income-hero-dot" />
              <span class="text-xs font-medium text-color-income-hero-text">本月可支配餘額</span>
            </div>
            <p class="text-4xl font-bold text-text-on-dark tracking-tight">
              {{ formatSummaryAmount(disposableBalance) }}
            </p>
            <p v-if="dashboardSummary?.exchangeRateIsStale" class="text-xs text-color-warning-text">
              此摘要使用過期匯率{{ dashboardSummary.exchangeRateUpdatedAt ? `，更新於 ${timeZone.formatDateTime(dashboardSummary.exchangeRateUpdatedAt)}` : '' }}
            </p>
            <div class="flex items-center gap-4">
              <div v-if="comparisonPct !== null" class="flex items-center gap-1.5">
                <Icon
                  :name="comparisonPct >= 0 ? 'TrendingUp' : 'TrendingDown'"
                  :size="15"
                  :class="comparisonPct >= 0 ? 'text-color-income-hero-fg' : 'text-color-expense-hero-fg'"
                />
                <span :class="comparisonPct >= 0 ? 'text-color-income-hero-fg' : 'text-color-expense-hero-fg'" class="text-xs">
                  較上月 {{ comparisonPct >= 0 ? '+' : '' }}{{ comparisonPct.toFixed(1) }}%
                </span>
              </div>
              <span class="w-px h-3 bg-bg-hero-divider" />
              <div class="flex items-center gap-1.5">
                <Icon name="CircleDot" :size="14" class="text-text-on-hero-muted" />
                <span class="text-xs text-text-on-hero-muted">
                  {{ summaryQuery.lastSuccessAt.value ? timeZone.formatDateTime(new Date(summaryQuery.lastSuccessAt.value)) : '尚未成功更新' }}
                </span>
              </div>
            </div>
          </div>
          <div class="flex gap-2.5">
            <button
              class="inline-flex items-center gap-1.5 bg-accent-primary hover:bg-accent-primary-hover text-text-on-accent text-xs font-semibold rounded-lg px-3.5 py-2 cursor-pointer"
              @click="router.push('/reports')"
            >
              <Icon name="FileText" :size="14" />
              查看報表
            </button>
          </div>
        </div>
        <div data-testid="dashboard-hero-details" class="flex w-full flex-col justify-center gap-2.5 px-7 pb-7 md:w-[280px] md:px-0 md:pb-0 md:pr-6">
          <div class="flex items-center gap-3">
            <div class="w-9 h-9 rounded-lg bg-color-income-hero-icon-bg flex items-center justify-center">
              <Icon name="TrendingDown" :size="18" class="text-color-income-hero-fg" />
            </div>
            <div>
               <p class="text-xs text-text-on-hero-muted">本期提款</p>
               <p class="text-base font-bold text-text-on-dark">{{ formatSummaryAmount(totalWithdrawals) }}</p>
            </div>
          </div>
          <div class="flex items-center gap-3">
            <div class="w-9 h-9 rounded-lg bg-color-expense-hero-icon-bg flex items-center justify-center">
              <Icon name="Receipt" :size="18" class="text-color-expense-hero-fg" />
            </div>
            <div>
              <p class="text-xs text-text-on-hero-muted">本期支出</p>
               <p class="text-base font-bold text-text-on-dark">{{ formatSummaryAmount(totalExpenses) }}</p>
            </div>
          </div>
          <div class="flex items-center gap-3">
            <div class="w-9 h-9 rounded-lg bg-color-credit-hero-icon-bg flex items-center justify-center">
              <Icon name="CreditCard" :size="18" class="text-color-credit-hero-fg" />
            </div>
            <div>
              <p class="text-xs text-text-on-hero-muted">本期分期</p>
               <p class="text-base font-bold text-text-on-dark">{{ formatSummaryAmount(installmentMonthlyDue) }}</p>
            </div>
          </div>
        </div>
        </div>
      </QueryState>

      <!-- Cards Row -->
      <div class="grid grid-cols-1 gap-5 xl:grid-cols-[340px_minmax(0,1fr)_minmax(0,1fr)]">
        <!-- Withdraw Card -->
        <div data-testid="dashboard-activity-card" class="min-w-0 bg-bg-card rounded-2xl border border-border-subtle overflow-hidden flex flex-col">
          <QueryState
            :status="withdrawalsQuery.status.value"
            :error-message="queryErrorMessage(withdrawalsQuery.error.value)"
            :last-success-at="withdrawalsQuery.lastSuccessAt.value"
            :retry="withdrawalsQuery.retry"
          >
          <div class="flex items-center gap-4 px-5 py-4 bg-gradient-to-br from-color-income-panel-start to-color-income-panel-end">
            <div class="flex items-center gap-3.5 flex-1 min-w-0">
              <div class="w-11 h-11 rounded-xl bg-color-income flex items-center justify-center shrink-0">
                <Icon name="TrendingDown" :size="22" class="text-text-on-accent" />
              </div>
              <div class="min-w-0">
                <div class="flex items-center gap-2">
                  <p class="text-base font-bold text-color-income-text">提款</p>
                    <span class="bg-bg-card text-color-income-text text-[10px] font-semibold rounded-full px-2 py-0.5">{{ dashboardSummary ? dashboardSummary.withdrawalCount : '—' }} 筆</span>
                </div>
                <p class="text-xs text-color-income-text">Withdrawals</p>
              </div>
            </div>
            <div class="text-right">
               <p class="text-[10px] text-color-income-text">本期提款合計</p>
              <p class="text-2xl font-bold text-color-income-text">{{ formatSummaryAmount(totalWithdrawals) }}</p>
            </div>
          </div>
          <div
            v-for="w in recentWithdrawals"
            :key="w.id"
            class="flex items-center gap-2 px-5 py-3 border-t border-border-subtle cursor-pointer hover:bg-bg-raised transition-colors"
            @click="router.push('/withdrawals')"
          >
            <span class="text-[11px] font-medium text-color-income-text bg-color-income-bg rounded px-2 py-0.5 truncate max-w-24">
              {{ w.bankAccount.bankName }}
            </span>
            <span class="text-xs text-text-secondary flex-1 text-right">
              {{ formatDateMMDD(w.date) }}
            </span>
            <span class="text-sm font-bold text-text-primary">{{ formatCurrency(w.amount, w.bankAccount.currencyCode ?? 'TWD') }}</span>
          </div>
          <div
            v-if="recentWithdrawals.length === 0"
            class="px-5 py-6 text-center text-xs text-text-tertiary border-t border-border-subtle"
          >
            尚無提款記錄
          </div>
          </QueryState>
        </div>

        <!-- Expense Card -->
        <div data-testid="dashboard-activity-card" class="min-w-0 bg-bg-card rounded-2xl border border-border-subtle overflow-hidden flex flex-col">
          <QueryState
            :status="expensesQuery.status.value"
            :error-message="queryErrorMessage(expensesQuery.error.value)"
            :last-success-at="expensesQuery.lastSuccessAt.value"
            :retry="expensesQuery.retry"
          >
          <div class="flex items-center gap-4 px-5 py-4 bg-gradient-to-br from-color-expense-panel-start to-color-expense-panel-end">
            <div class="flex items-center gap-3.5 flex-1 min-w-0">
              <div class="w-11 h-11 rounded-xl bg-color-expense-action flex items-center justify-center shrink-0">
                <Icon name="Receipt" :size="22" class="text-color-expense-action-text" />
              </div>
              <div class="min-w-0">
                <div class="flex items-center gap-2">
                  <p class="text-base font-bold text-color-expense-text">支出</p>
                    <span class="bg-bg-card text-color-expense-text text-[10px] font-semibold rounded-full px-2 py-0.5">{{ dashboardSummary ? dashboardSummary.expenseCount : '—' }} 筆</span>
                </div>
                <p class="text-xs text-color-expense-text">Expenses</p>
              </div>
            </div>
            <div class="text-right">
               <p class="text-[10px] text-color-expense-text">本期支出合計</p>
              <p class="text-2xl font-bold text-color-expense-text">{{ formatSummaryAmount(totalExpenses) }}</p>
            </div>
          </div>
          <div class="flex items-center gap-3 px-5 py-2.5 bg-bg-raised border-t border-border-subtle text-[10px] font-semibold text-text-tertiary uppercase tracking-wider">
            <span class="w-10">日期</span>
            <span class="flex-1">類別</span>
            <span class="text-right w-20">金額</span>
          </div>
          <div
            v-for="e in recentExpenses"
            :key="e.id"
            class="flex items-center gap-3 px-5 py-3 border-t border-border-subtle cursor-pointer hover:bg-bg-raised transition-colors"
            @click="router.push('/transactions')"
          >
            <span class="text-xs text-text-secondary w-10">{{ formatDateMMDD(e.date) }}</span>
            <div class="flex-1 min-w-0">
              <p class="text-xs text-text-secondary">{{ e.category.name }}</p>
              <p class="text-sm font-semibold text-text-primary truncate">{{ e.description || '—' }}</p>
            </div>
            <span class="text-sm font-bold text-color-expense-text text-right w-20">{{ formatMoney(e.amount) }}</span>
          </div>
          <div
            v-if="recentExpenses.length === 0"
            class="px-5 py-6 text-center text-xs text-text-tertiary border-t border-border-subtle"
          >
            尚無支出記錄
          </div>
          </QueryState>
        </div>

        <!-- Installment Card -->
        <div data-testid="dashboard-activity-card" class="min-w-0 bg-bg-card rounded-2xl border border-border-subtle overflow-hidden flex flex-col">
          <QueryState
            :status="installmentsQuery.status.value"
            :error-message="queryErrorMessage(installmentsQuery.error.value)"
            :last-success-at="installmentsQuery.lastSuccessAt.value"
            :retry="installmentsQuery.retry"
          >
          <div class="flex items-center gap-4 px-5 py-4 bg-gradient-to-br from-color-credit-panel-start to-color-credit-panel-end">
            <div class="flex items-center gap-3.5 flex-1 min-w-0">
              <div class="w-11 h-11 rounded-xl bg-color-credit flex items-center justify-center shrink-0">
                <Icon name="CreditCard" :size="22" class="text-text-on-accent" />
              </div>
              <div class="min-w-0">
                <div class="flex items-center gap-2">
                  <p class="text-base font-bold text-color-credit-text">信用卡分期</p>
                  <span class="bg-bg-card text-color-credit-text text-[10px] font-semibold rounded-full px-2 py-0.5">
                    {{ dashboardSummary ? dashboardSummary.activeInstallmentCount : '—' }} 筆
                  </span>
                </div>
                <p class="text-xs text-color-credit-text">Credit Card Installments</p>
              </div>
            </div>
            <div class="text-right">
              <button
                class="text-[10px] text-color-credit-text hover:text-text-primary underline underline-offset-2 cursor-pointer"
                @click="router.push('/installments')"
              >
                檢視全部
              </button>
              <p class="text-2xl font-bold text-color-credit-text">{{ formatSummaryAmount(installmentMonthlyDue) }}</p>
            </div>
          </div>
          <div class="flex items-center gap-2 px-5 py-2.5 bg-bg-raised border-t border-border-subtle text-[10px] font-semibold text-text-tertiary uppercase tracking-wider">
            <span class="w-10">日期</span>
            <span class="flex-1">項目 / 摘要</span>
            <span class="text-right w-14">總額</span>
            <span class="text-center w-12">期數</span>
            <span class="text-center w-12">已繳</span>
            <span class="text-right w-16">本期</span>
          </div>
          <div
            v-for="i in recentInstallments"
            :key="i.id"
            class="flex items-center gap-2 px-5 py-3 border-t border-border-subtle cursor-pointer hover:bg-bg-raised transition-colors"
            @click="router.push('/installments')"
          >
            <span class="text-xs text-text-secondary w-10">{{ i.transaction?.date ? formatDateMMDD(i.transaction.date) : formatEventDateMMDD(i.createdAt) }}</span>
            <div class="flex-1 min-w-0">
              <p class="text-sm font-semibold text-text-primary truncate">{{ i.description || '—' }}</p>
            </div>
            <span class="text-xs text-text-secondary text-right w-14">{{ formatMoney(i.totalAmount) }}</span>
            <span class="w-12 flex justify-center">
              <span class="text-[11px] font-semibold text-color-credit-text bg-color-credit-bg rounded px-2 py-0.5">{{ i.periods }} 期</span>
            </span>
            <span class="text-xs font-semibold text-text-primary text-center w-12">{{ progressLabel(i) }}</span>
            <span class="text-sm font-bold text-color-credit-text text-right w-16">{{ formatMoney(i.perPeriod) }}</span>
          </div>
          <div
            v-if="recentInstallments.length === 0"
            class="px-5 py-6 text-center text-xs text-text-tertiary border-t border-border-subtle"
          >
            尚無分期記錄
          </div>
          </QueryState>
        </div>
      </div>

      <!-- Complete-period summary footer -->
      <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        <div class="bg-bg-card rounded-xl border border-border-subtle px-4 py-3">
          <div class="flex items-center gap-2">
            <Icon name="TrendingDown" :size="15" class="text-color-income-text" />
            <p class="text-xs text-text-secondary">提款合計</p>
          </div>
          <p class="mt-1 text-lg font-bold text-color-income-text">{{ formatSummaryAmount(totalWithdrawals) }}</p>
          <p class="text-[11px] text-text-tertiary">{{ dashboardSummary ? dashboardSummary.withdrawalCount : '—' }} 筆</p>
        </div>
        <div class="bg-bg-card rounded-xl border border-border-subtle px-4 py-3">
          <div class="flex items-center gap-2">
            <Icon name="Receipt" :size="15" class="text-color-expense-text" />
            <p class="text-xs text-text-secondary">支出合計</p>
          </div>
          <p class="mt-1 text-lg font-bold text-color-expense-text">{{ formatSummaryAmount(totalExpenses) }}</p>
          <p class="text-[11px] text-text-tertiary">{{ dashboardSummary ? dashboardSummary.expenseCount : '—' }} 筆</p>
        </div>
        <div class="bg-bg-card rounded-xl border border-border-subtle px-4 py-3">
          <div class="flex items-center gap-2">
            <Icon name="Wallet" :size="15" class="text-text-secondary" />
            <p class="text-xs text-text-secondary">剩餘合計</p>
          </div>
          <p class="mt-1 text-lg font-bold" :class="disposableBalance !== null && disposableBalance >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">
            {{ formatSummaryAmount(disposableBalance) }}
          </p>
          <p class="text-[11px] text-text-tertiary">提款減支出</p>
        </div>
        <div class="bg-bg-card rounded-xl border border-border-subtle px-4 py-3">
          <div class="flex items-center gap-2">
            <Icon name="CreditCard" :size="15" class="text-color-credit-text" />
            <p class="text-xs text-text-secondary">信用卡分期</p>
          </div>
          <p class="mt-1 text-lg font-bold text-color-credit-text">{{ formatSummaryAmount(installmentMonthlyDue) }}</p>
          <p class="text-[11px] text-text-tertiary">{{ dashboardSummary ? dashboardSummary.installmentDuePaymentCount : '—' }} 筆未繳應付款</p>
        </div>
      </div>

    </template>
  </div>
</template>
