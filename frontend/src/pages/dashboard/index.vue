<script setup lang="ts">
import { ref, computed, watch, onMounted, inject } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '../../api'
import type { Withdrawal, Transaction, Installment } from '../../types'
import Icon from '../../components/ui/Icon.vue'
import { formatMoney } from '../../utils/format'
import { formatDateOnly, getSystemDateParts } from '../../utils/timezone'
import { useTimeZone } from '../../composables/useTimeZone'

const router = useRouter()
const toast = inject<{ error: (m: string) => void }>('toast')!
const timeZone = useTimeZone()
const initialSystemDate = getSystemDateParts(new Date(), timeZone.timeZoneId.value)

const year = ref(initialSystemDate.year)
const month = ref(initialSystemDate.month)

function getMonthStart(y: number, m: number): string {
  return `${y}-${String(m).padStart(2, '0')}-01`
}
function getMonthEnd(y: number, m: number): string {
  const d = new Date(y, m, 0)
  return `${y}-${String(m).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}
function prevMonthKey(y: number, m: number): { year: number; month: number } {
  return m === 1 ? { year: y - 1, month: 12 } : { year: y, month: m - 1 }
}

const startDate = computed(() => getMonthStart(year.value, month.value))
const endDate = computed(() => getMonthEnd(year.value, month.value))

const prevKey = computed(() => prevMonthKey(year.value, month.value))
const prevStart = computed(() => getMonthStart(prevKey.value.year, prevKey.value.month))
const prevEnd = computed(() => getMonthEnd(prevKey.value.year, prevKey.value.month))

function goPrev() {
  const k = prevMonthKey(year.value, month.value)
  year.value = k.year; month.value = k.month
}
function goNext() {
  if (month.value === 12) { year.value++; month.value = 1 }
  else month.value++
}
function goCurrent() {
  const current = getSystemDateParts(new Date(), timeZone.timeZoneId.value)
  year.value = current.year
  month.value = current.month
}
const isCurrentMonth = computed(() =>
  year.value === getSystemDateParts(new Date(), timeZone.timeZoneId.value).year
  && month.value === getSystemDateParts(new Date(), timeZone.timeZoneId.value).month
)

const loading = ref(true)
const withdrawals = ref<Withdrawal[]>([])
const expenses = ref<Transaction[]>([])
const activeInstallments = ref<Installment[]>([])
const prevWithdrawalsTotal = ref(0)
const prevExpensesTotal = ref(0)

async function loadData() {
  loading.value = true
  try {
    const [wd, exp, inst, prevWdRes] = await Promise.all([
      api.withdrawals.list({ page: 1, startDate: startDate.value, endDate: endDate.value, pageSize: 50 }),
      api.transactions.list({ page: 1, startDate: startDate.value, endDate: endDate.value, type: 'Expense', pageSize: 50 }),
      api.installments.list({ page: 1, status: 'Active', pageSize: 50 }),
      api.withdrawals.list({ page: 1, startDate: prevStart.value, endDate: prevEnd.value, pageSize: 50 }),
    ])
    withdrawals.value = wd.items ?? []
    expenses.value = exp.items ?? []
    activeInstallments.value = inst.items ?? []
    prevWithdrawalsTotal.value = (prevWdRes.items ?? []).reduce((s, w) => s + w.amount, 0)
    const prevM = prevKey.value
    try {
      const ps = await api.reports.monthlySummary({ year: prevM.year, month: prevM.month })
      prevExpensesTotal.value = ps.totalExpense
    } catch {
      prevExpensesTotal.value = 0
    }
  } catch {
    toast.error('載入儀表板資料失敗')
  } finally {
    loading.value = false
  }
}

watch([year, month], loadData)
onMounted(loadData)

const totalWithdrawals = computed(() =>
  withdrawals.value.reduce((s, w) => s + w.amount, 0)
)
const totalExpenses = computed(() =>
  expenses.value.reduce((s, e) => s + e.amount, 0)
)
const disposableBalance = computed(() => totalWithdrawals.value - totalExpenses.value)

const prevDisposable = computed(() =>
  prevWithdrawalsTotal.value - prevExpensesTotal.value
)
const comparisonPct = computed(() => {
  if (prevDisposable.value === 0) return null
  return ((disposableBalance.value - prevDisposable.value) / Math.abs(prevDisposable.value) * 100)
})

const installmentMonthlyDue = computed(() =>
  activeInstallments.value.reduce((s, i) => s + i.perPeriod, 0)
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

function progressLabel(i: Installment): string {
  const paid = i.periods - i.remainingPeriods
  return `${paid}/${i.periods}`
}

function formatDateMMDD(d: string): string {
  const formatted = formatDateOnly(d)
  return formatted.includes('/') ? formatted.slice(5) : d
}

// Formats an event timestamp as month/day in the configured system time zone.
function formatEventDateMMDD(timestamp: string): string {
  return timeZone.formatDateTime(timestamp).slice(5)
}
</script>

<template>
  <div class="p-6 space-y-6">
    <!-- Header -->
    <div class="flex items-start justify-between">
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
            class="p-0.5 text-text-secondary hover:text-text-primary cursor-pointer disabled:opacity-30 disabled:cursor-not-allowed"
            :disabled="loading"
            @click="goPrev"
          >
            <Icon name="ChevronLeft" :size="16" />
          </button>
          <button
            class="text-xs font-medium text-text-primary px-2 cursor-pointer disabled:opacity-50"
            :disabled="isCurrentMonth"
            @click="goCurrent"
          >
            {{ year }}/{{ String(month).padStart(2, '0') }}
          </button>
          <button
            class="p-0.5 text-text-secondary hover:text-text-primary cursor-pointer disabled:opacity-30 disabled:cursor-not-allowed"
            :disabled="isCurrentMonth || loading"
            @click="goNext"
          >
            <Icon name="ChevronRight" :size="16" />
          </button>
        </div>
        <button
          class="flex items-center gap-1.5 bg-bg-card border border-border-subtle rounded-lg px-3.5 py-2 text-xs font-medium text-text-primary hover:bg-gray-50 dark:hover:bg-gray-800 cursor-pointer"
          @click="router.push('/reports')"
        >
          <Icon name="ChartColumn" :size="15" class="text-text-secondary" />
          查看報表
        </button>
      </div>
    </div>

    <div v-if="loading" class="flex items-center justify-center py-32">
      <Icon name="Loader2" :size="32" class="animate-spin text-text-secondary" />
    </div>

    <template v-else>
      <!-- Hero Card -->
      <div
        class="flex rounded-2xl overflow-hidden"
        style="background: linear-gradient(135deg, #0F172A, #1E293B 50%, #065F46)"
      >
        <div class="flex-1 flex flex-col justify-between p-7 gap-3">
          <div class="space-y-3">
            <div class="inline-flex items-center gap-1.5 bg-emerald-500/20 dark:bg-emerald-500/10 rounded-full px-3 py-1">
              <span class="w-1.5 h-1.5 rounded-full bg-emerald-400 dark:bg-emerald-300" />
              <span class="text-xs font-medium text-emerald-300">本月可支配餘額</span>
            </div>
            <p class="text-4xl font-bold text-white tracking-tight">
              {{ formatMoney(disposableBalance) }}
            </p>
            <div class="flex items-center gap-4">
              <div v-if="comparisonPct !== null" class="flex items-center gap-1.5">
                <Icon
                  :name="comparisonPct >= 0 ? 'TrendingUp' : 'TrendingDown'"
                  :size="15"
                  :class="comparisonPct >= 0 ? 'text-emerald-400' : 'text-red-400'"
                />
                <span :class="comparisonPct >= 0 ? 'text-emerald-400' : 'text-red-400'" class="text-xs">
                  較上月 {{ comparisonPct >= 0 ? '+' : '' }}{{ comparisonPct.toFixed(1) }}%
                </span>
              </div>
              <span class="w-px h-3 bg-slate-600" />
              <div class="flex items-center gap-1.5">
                <Icon name="CircleDot" :size="14" class="text-slate-400" />
                <span class="text-xs text-slate-400">已更新於剛剛</span>
              </div>
            </div>
          </div>
          <div class="flex gap-2.5">
            <button
              class="inline-flex items-center gap-1.5 bg-emerald-500 hover:bg-emerald-600 dark:bg-emerald-600 dark:hover:bg-emerald-700 text-white text-xs font-semibold rounded-lg px-3.5 py-2 cursor-pointer"
              @click="router.push('/reports')"
            >
              <Icon name="FileText" :size="14" />
              查看報表
            </button>
          </div>
        </div>
        <div class="w-[280px] flex flex-col justify-center gap-2.5 pr-6">
          <div class="flex items-center gap-3">
            <div class="w-9 h-9 rounded-lg bg-emerald-500/30 flex items-center justify-center">
              <Icon name="TrendingDown" :size="18" class="text-emerald-400" />
            </div>
            <div>
              <p class="text-xs text-slate-400">本期收入</p>
              <p class="text-base font-bold text-white">{{ formatMoney(totalWithdrawals) }}</p>
            </div>
          </div>
          <div class="flex items-center gap-3">
            <div class="w-9 h-9 rounded-lg bg-red-500/30 flex items-center justify-center">
              <Icon name="Receipt" :size="18" class="text-red-300" />
            </div>
            <div>
              <p class="text-xs text-slate-400">本期支出</p>
              <p class="text-base font-bold text-white">{{ formatMoney(totalExpenses) }}</p>
            </div>
          </div>
          <div class="flex items-center gap-3">
            <div class="w-9 h-9 rounded-lg bg-purple-500/30 flex items-center justify-center">
              <Icon name="CreditCard" :size="18" class="text-purple-300" />
            </div>
            <div>
              <p class="text-xs text-slate-400">本期分期</p>
              <p class="text-base font-bold text-white">{{ formatMoney(installmentMonthlyDue) }}</p>
            </div>
          </div>
        </div>
      </div>

      <!-- Cards Row -->
      <div class="flex gap-5">
        <!-- Withdraw Card -->
        <div class="w-[340px] bg-bg-card rounded-2xl border border-border-subtle overflow-hidden flex flex-col">
          <div class="flex items-center gap-4 px-5 py-4 bg-gradient-to-br from-[#ECFDF5] to-[#D1FAE5] dark:from-emerald-900/40 dark:to-emerald-800/30">
            <div class="flex items-center gap-3.5 flex-1 min-w-0">
              <div class="w-11 h-11 rounded-xl bg-emerald-500 dark:bg-emerald-700 flex items-center justify-center shrink-0">
                <Icon name="TrendingDown" :size="22" class="text-white" />
              </div>
              <div class="min-w-0">
                <div class="flex items-center gap-2">
                  <p class="text-base font-bold text-emerald-900 dark:text-emerald-100">提款</p>
                  <span class="bg-white dark:bg-gray-800 text-emerald-700 dark:text-emerald-300 text-[10px] font-semibold rounded-full px-2 py-0.5">{{ withdrawals.length }} 筆</span>
                </div>
                <p class="text-xs text-emerald-700 dark:text-emerald-300">Withdrawals</p>
              </div>
            </div>
            <div class="text-right">
              <p class="text-[10px] text-emerald-700 dark:text-emerald-300">本月提款合計</p>
              <p class="text-2xl font-bold text-emerald-900 dark:text-emerald-100">{{ formatMoney(totalWithdrawals) }}</p>
            </div>
          </div>
          <div
            v-for="w in recentWithdrawals"
            :key="w.id"
            class="flex items-center gap-2 px-5 py-3 border-t border-border-subtle cursor-pointer hover:bg-gray-50 dark:hover:bg-gray-800 transition-colors"
            @click="router.push('/withdrawals')"
          >
            <span class="text-[11px] font-medium text-emerald-600 bg-emerald-50 dark:bg-emerald-900/30 rounded px-2 py-0.5 truncate max-w-24">
              {{ w.bankAccount.bankName }}
            </span>
            <span class="text-xs text-text-secondary flex-1 text-right">
              {{ formatDateMMDD(w.date) }}
            </span>
            <span class="text-sm font-bold text-text-primary">{{ formatMoney(w.amount) }}</span>
          </div>
          <div
            v-if="recentWithdrawals.length === 0"
            class="px-5 py-6 text-center text-xs text-text-tertiary border-t border-border-subtle"
          >
            尚無提款記錄
          </div>
        </div>

        <!-- Expense Card -->
        <div class="flex-1 bg-bg-card rounded-2xl border border-border-subtle overflow-hidden flex flex-col">
          <div class="flex items-center gap-4 px-5 py-4 bg-gradient-to-br from-[#FEF2F2] to-[#FECACA] dark:from-red-900/40 dark:to-red-800/30">
            <div class="flex items-center gap-3.5 flex-1 min-w-0">
              <div class="w-11 h-11 rounded-xl bg-red-500 dark:bg-red-700 flex items-center justify-center shrink-0">
                <Icon name="Receipt" :size="22" class="text-white" />
              </div>
              <div class="min-w-0">
                <div class="flex items-center gap-2">
                  <p class="text-base font-bold text-red-900 dark:text-red-100">支出</p>
                  <span class="bg-white dark:bg-gray-800 text-red-700 dark:text-red-300 text-[10px] font-semibold rounded-full px-2 py-0.5">{{ expenses.length }} 筆</span>
                </div>
                <p class="text-xs text-red-700 dark:text-red-300">Expenses</p>
              </div>
            </div>
            <div class="text-right">
              <p class="text-[10px] text-red-700 dark:text-red-300">本月支出合計</p>
              <p class="text-2xl font-bold text-red-900 dark:text-red-100">{{ formatMoney(totalExpenses) }}</p>
            </div>
          </div>
          <div class="flex items-center gap-3 px-5 py-2.5 bg-gray-50 dark:bg-gray-800/50 border-t border-border-subtle text-[10px] font-semibold text-text-tertiary uppercase tracking-wider">
            <span class="w-10">日期</span>
            <span class="flex-1">類別</span>
            <span class="text-right w-20">金額</span>
          </div>
          <div
            v-for="e in recentExpenses"
            :key="e.id"
            class="flex items-center gap-3 px-5 py-3 border-t border-border-subtle cursor-pointer hover:bg-gray-50 dark:hover:bg-gray-800 transition-colors"
            @click="router.push('/transactions')"
          >
            <span class="text-xs text-text-secondary w-10">{{ formatDateMMDD(e.date) }}</span>
            <div class="flex-1 min-w-0">
              <p class="text-xs text-text-secondary">{{ e.category.name }}</p>
              <p class="text-sm font-semibold text-text-primary truncate">{{ e.description || '—' }}</p>
            </div>
            <span class="text-sm font-bold text-color-expense text-right w-20">{{ formatMoney(e.amount) }}</span>
          </div>
          <div
            v-if="recentExpenses.length === 0"
            class="px-5 py-6 text-center text-xs text-text-tertiary border-t border-border-subtle"
          >
            尚無支出記錄
          </div>
        </div>

        <!-- Installment Card -->
        <div class="flex-1 bg-bg-card rounded-2xl border border-border-subtle overflow-hidden flex flex-col">
          <div class="flex items-center gap-4 px-5 py-4 bg-gradient-to-br from-[#F5F3FF] to-[#DDD6FE] dark:from-purple-900/40 dark:to-purple-800/30">
            <div class="flex items-center gap-3.5 flex-1 min-w-0">
              <div class="w-11 h-11 rounded-xl bg-purple-600 dark:bg-purple-700 flex items-center justify-center shrink-0">
                <Icon name="CreditCard" :size="22" class="text-white" />
              </div>
              <div class="min-w-0">
                <div class="flex items-center gap-2">
                  <p class="text-base font-bold text-purple-900 dark:text-purple-100">信用卡分期</p>
                  <span class="bg-white dark:bg-gray-800 text-purple-700 dark:text-purple-300 text-[10px] font-semibold rounded-full px-2 py-0.5">
                    {{ activeInstallments.length }} 筆
                  </span>
                </div>
                <p class="text-xs text-purple-700 dark:text-purple-300">Credit Card Installments</p>
              </div>
            </div>
            <div class="text-right">
              <p class="text-[10px] text-purple-700 dark:text-purple-300">本期應繳金額</p>
              <p class="text-2xl font-bold text-purple-900 dark:text-purple-100">{{ formatMoney(installmentMonthlyDue) }}</p>
            </div>
          </div>
          <div class="flex items-center gap-2 px-5 py-2.5 bg-gray-50 dark:bg-gray-800/50 border-t border-border-subtle text-[10px] font-semibold text-text-tertiary uppercase tracking-wider">
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
            class="flex items-center gap-2 px-5 py-3 border-t border-border-subtle cursor-pointer hover:bg-gray-50 dark:hover:bg-gray-800 transition-colors"
            @click="router.push('/installments')"
          >
            <span class="text-xs text-text-secondary w-10">{{ i.transaction?.date ? formatDateMMDD(i.transaction.date) : formatEventDateMMDD(i.createdAt) }}</span>
            <div class="flex-1 min-w-0">
              <p class="text-sm font-semibold text-text-primary truncate">{{ i.description || '—' }}</p>
            </div>
            <span class="text-xs text-text-secondary text-right w-14">{{ formatMoney(i.totalAmount) }}</span>
            <span class="w-12 flex justify-center">
              <span class="text-[11px] font-semibold text-purple-700 bg-purple-50 dark:bg-purple-900/30 rounded px-2 py-0.5">{{ i.periods }} 期</span>
            </span>
            <span class="text-xs font-semibold text-text-primary text-center w-12">{{ progressLabel(i) }}</span>
            <span class="text-sm font-bold text-purple-700 text-right w-16">{{ formatMoney(i.perPeriod) }}</span>
          </div>
          <div
            v-if="recentInstallments.length === 0"
            class="px-5 py-6 text-center text-xs text-text-tertiary border-t border-border-subtle"
          >
            尚無分期記錄
          </div>
        </div>
      </div>

    </template>
  </div>
</template>
