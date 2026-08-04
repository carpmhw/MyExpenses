<script setup lang="ts">
import { ref, computed, onMounted, watch, onScopeDispose, inject } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ApiError, api } from '../../api'
import type { Category, Transaction, PaymentMethod, CreditCard } from '../../types'
import Card from '../../components/ui/Card.vue'
import Button from '../../components/ui/Button.vue'
import DataTable from '../../components/ui/DataTable.vue'
import Modal from '../../components/ui/Modal.vue'
import ConfirmDialog from '../../components/ui/ConfirmDialog.vue'
import Icon from '../../components/ui/Icon.vue'
import TransactionForm from '../../components/transactions/TransactionForm.vue'
import { usePagination } from '../../composables/usePagination'
import { formatMoney } from '../../utils/format'
import { addCalendarDays, getCurrentMonthRange } from '../../utils/timezone'
import { useTimeZone } from '../../composables/useTimeZone'
import { createIdempotencyKeyState } from '../../utils/idempotency'
import { useAsyncQuery } from '../../composables/useAsyncQuery'
import { useAsyncMutation } from '../../composables/useAsyncMutation'
import {
  createInitialTransactionForm,
  createTransactionFormFromItem,
  type TransactionFormCommand,
  type TransactionFormValues,
} from '../../utils/transactionForm'

const toast = inject<{ success: (m: string) => void; error: (m: string) => void }>('toast')!
const timeZone = useTimeZone()


const route = useRoute()
const router = useRouter()
const pagination = usePagination(1, 15)

const categories = ref<Category[]>([])
const paymentMethods = ref<PaymentMethod[]>([])
const creditCards = ref<CreditCard[]>([])
const categoriesLoading = ref(false)
const paymentMethodsLoading = ref(false)
const creditCardsLoading = ref(false)
const categoriesError = ref<string | null>(null)
const paymentMethodsError = ref<string | null>(null)
const creditCardsError = ref<string | null>(null)

const activeTab = ref<'all' | 'Income' | 'Expense'>((route.query.type as 'all' | 'Income' | 'Expense') || 'all')
const search = ref((route.query.search as string) || '')
const selectedCategory = ref((route.query.categoryId as string) || '')
const startDate = ref((route.query.startDate as string) || getDefaultStartDate())
const endDate = ref((route.query.endDate as string) || getDefaultEndDate())
const debouncedSearch = ref(search.value)
let searchTimer: ReturnType<typeof setTimeout> | null = null
// 在日期範圍成為查詢身份前先驗證起訖日期。
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

const modalOpen = ref(false)
const editingItem = ref<Transaction | null>(null)
const form = ref<TransactionFormValues>(createInitialTransactionForm(timeZone.getToday(), categories.value))
const formKey = ref(0)
const submissionError = ref<string | null>(null)
const submissionNotice = ref<string | null>(null)
const uncertainSubmissionKind = ref<'ordinary' | 'purchase' | null>(null)

const confirmOpen = ref(false)
const deletingId = ref<number | null>(null)

const columns = [
  { key: 'seq', label: '序號' },
  { key: 'date', label: '日期' },
  { key: 'category', label: '類別' },
  { key: 'type', label: '類型' },
  { key: 'item', label: '項目' },
  { key: 'amount', label: '金額', align: 'right' as const },
  { key: 'paymentMethod', label: '支付方式' },
  { key: 'notes', label: '備註' },
]

const installmentPurchaseIdempotency = createIdempotencyKeyState()

type TransactionMutationInput =
  | Extract<TransactionFormCommand, { kind: 'create' }>
  | (Extract<TransactionFormCommand, { kind: 'purchase' }> & { idempotencyKey: string })
  | Extract<TransactionFormCommand, { kind: 'update' }>

const transactionQuery = useAsyncQuery({
  key: () => [
    'transactions',
    pagination.page.value,
    pagination.pageSize.value,
    activeTab.value,
    selectedCategory.value,
    startDate.value,
    endDate.value,
    debouncedSearch.value,
  ],
  query: ({ signal }) => api.transactions.list({
    page: pagination.page.value,
    pageSize: pagination.pageSize.value,
    categoryId: selectedCategory.value ? Number(selectedCategory.value) : undefined,
    startDate: startDate.value || undefined,
    endDate: endDate.value || undefined,
    search: debouncedSearch.value || undefined,
    type: activeTab.value !== 'all' ? activeTab.value : undefined,
  }, { signal }),
  isEmpty: result => result.items.length === 0,
})

const transactions = computed(() => transactionQuery.data.value?.items ?? [])
const summary = computed(() => transactionQuery.data.value?.summary ?? null)
const loading = computed(() => transactionQuery.status.value === 'loading' || transactionQuery.status.value === 'refreshing')
const saving = computed(() => transactionMutation.status.value === 'submitting' || deleteMutation.status.value === 'submitting')
const transactionListError = computed(() => transactionQuery.status.value === 'stale'
  ? '資料可能已過期，請重新整理。'
  : transactionQuery.status.value === 'error'
    ? queryErrorMessage(transactionQuery.error.value)
    : null)

const transactionMutation = useAsyncMutation<TransactionMutationInput, unknown>({
  mutate: input => {
    if (input.kind === 'create') return api.transactions.create(input.data)
    if (input.kind === 'purchase') return api.installmentPurchases.create(input.data, input.idempotencyKey)
    return api.transactions.update(input.id, input.data)
  },
  classifyError: error => ({ uncertain: !(error instanceof ApiError && [400, 401, 403, 404, 409, 422].includes(error.status ?? 0)) }),
})

const deleteMutation = useAsyncMutation<number, void>({
  mutate: id => api.transactions.delete(id),
  classifyError: error => ({ uncertain: !(error instanceof ApiError && [400, 401, 403, 404, 409, 422].includes(error.status ?? 0)) }),
})

const referenceDataLoading = computed(() => categoriesLoading.value || paymentMethodsLoading.value)
const referenceDataReady = computed(() => !referenceDataLoading.value && !categoriesError.value && !paymentMethodsError.value)
const referenceDataError = computed(() => categoriesError.value || paymentMethodsError.value ? '分類或支付方式資料載入失敗，請重試。' : null)
const creditCardDataReady = computed(() => !creditCardsLoading.value && !creditCardsError.value)
const creditCardDataError = computed(() => creditCardsError.value ? '信用卡資料載入失敗，請重試。' : null)
const submissionUncertain = computed(() => uncertainSubmissionKind.value !== null)
const submissionRetryAllowed = computed(() => uncertainSubmissionKind.value === 'purchase')

watch(() => transactionQuery.data.value?.total, total => {
  if (total !== undefined) pagination.total.value = total
})

const stats = computed(() => {
  if (!summary.value) {
    return { total: null, income: null, expense: null, count: null, dailyAvg: null, max: null }
  }
  if (activeTab.value === 'all') {
    return {
      total: summary.value.totalAmount,
      income: summary.value.totalIncome,
      expense: summary.value.totalExpense,
      count: summary.value.count,
      dailyAvg: 0,
      max: 0,
    }
  }
  const total = activeTab.value === 'Income' ? summary.value.totalIncome : summary.value.totalExpense
  return {
    total,
    income: 0,
    expense: 0,
    count: summary.value.count,
    max: summary.value.maxAmount,
    dailyAvg: summary.value.dailyAverage,
  }
})

// 格式化可為空的交易摘要，避免失敗查詢被誤顯示為零。
function formatSummaryValue(value: number | null): string {
  return value === null ? '—' : formatMoney(value)
}

// 將查詢錯誤轉成交易表格可安全顯示的訊息。
function queryErrorMessage(error: unknown): string {
  return error instanceof ApiError ? error.userMessage : '交易資料載入失敗，請重試。'
}

// 取得設定時區中的預設交易起始日期。
function getDefaultStartDate() {
  return getCurrentMonthRange(new Date(), timeZone.timeZoneId.value).start
}

// 取得設定時區中的預設交易結束日期。
function getDefaultEndDate() {
  return getCurrentMonthRange(new Date(), timeZone.timeZoneId.value).end
}

// 載入交易表單使用的分類選項並保留失敗狀態。
async function fetchCategories() {
  categoriesLoading.value = true
  categoriesError.value = null
  try {
    const result = await api.categories.list({ pageSize: 999 })
    categories.value = result.items
  } catch (error) {
    categoriesError.value = error instanceof ApiError ? error.userMessage : '分類資料載入失敗，請重試。'
  } finally {
    categoriesLoading.value = false
  }
}

// 將交易篩選條件同步到路由而不改變查詢擁有者。
function syncQueryString() {
  router.replace({
    query: {
      ...(activeTab.value !== 'all' ? { type: activeTab.value } : {}),
      ...(search.value ? { search: search.value } : {}),
      ...(selectedCategory.value ? { categoryId: selectedCategory.value } : {}),
      ...(startDate.value ? { startDate: startDate.value } : {}),
      ...(endDate.value ? { endDate: endDate.value } : {}),
      ...(pagination.page.value > 1 ? { page: String(pagination.page.value) } : {}),
    },
  })
}

watch([selectedCategory, startDate, endDate, activeTab], () => {
  pagination.reset()
  syncQueryString()
})

watch(() => pagination.page.value, () => {
  syncQueryString()
})

watch(search, value => {
  if (searchTimer) clearTimeout(searchTimer)
  searchTimer = setTimeout(() => {
    debouncedSearch.value = value
    pagination.reset()
    syncQueryString()
  }, 300)
})

onScopeDispose(() => {
  if (searchTimer) clearTimeout(searchTimer)
})

// 重置交易表單並開始一次新的建立操作。
function openCreate() {
  editingItem.value = null
  form.value = createInitialTransactionForm(
    timeZone.getToday(),
    categories.value,
    activeTab.value !== 'all' ? activeTab.value : 'Expense',
  )
  submissionError.value = null
  submissionNotice.value = null
  uncertainSubmissionKind.value = null
  installmentPurchaseIdempotency.begin()
  transactionMutation.reset()
  formKey.value += 1
  modalOpen.value = true
}

// 將伺服器交易載入編輯表單並清除殘留的建立狀態。
function openEdit(item: Transaction) {
  editingItem.value = item
  form.value = createTransactionFormFromItem(item)
  submissionError.value = null
  submissionNotice.value = null
  uncertainSubmissionKind.value = null
  installmentPurchaseIdempotency.begin()
  transactionMutation.reset()
  formKey.value += 1
  modalOpen.value = true
}

// 執行表單已驗證的交易命令並誠實區分各種結果。
async function save(command: TransactionFormCommand) {
  submissionError.value = null
  submissionNotice.value = null
  uncertainSubmissionKind.value = null
  let mutationSucceeded = false
  try {
    let input: TransactionMutationInput
    if (command.kind === 'purchase') {
      const idempotencyKey = installmentPurchaseIdempotency.prepare(command.data)
      input = { ...command, idempotencyKey }
    } else input = command

    await transactionMutation.submit(input)
    uncertainSubmissionKind.value = null
    if (command.kind === 'update') toast.success('交易已更新')
    else if (command.kind === 'purchase') {
      installmentPurchaseIdempotency.clear()
      toast.success('交易與分期已建立')
    } else toast.success('交易已建立')
    modalOpen.value = false
    mutationSucceeded = true
  } catch (e) {
    if (transactionMutation.uncertain.value) {
      uncertainSubmissionKind.value = command.kind === 'purchase' ? 'purchase' : 'ordinary'
      submissionNotice.value = command.kind === 'purchase'
        ? '無法確認交易與分期是否已建立；可使用相同資料安全重試。'
        : '無法確認交易是否已建立；請先重新整理交易列表，避免重複送出。'
    } else {
      uncertainSubmissionKind.value = null
      submissionError.value = e instanceof ApiError ? e.userMessage : '儲存失敗'
      toast.error(submissionError.value)
    }
  }

  if (mutationSucceeded) {
    await transactionQuery.refresh()
    if (transactionQuery.status.value === 'stale') {
      toast.error('已儲存，但交易列表重新整理失敗')
    }
  }
}

// 開啟指定交易編號的刪除確認。
function confirmDelete(id: number) {
  deletingId.value = id
  confirmOpen.value = true
}

// 確認刪除交易後重新整理目前查詢身份。
async function doDelete() {
  if (deletingId.value !== null) {
    const id = deletingId.value
    try {
      await deleteMutation.submit(id)
      confirmOpen.value = false
      deletingId.value = null
      toast.success('交易已刪除')
      await transactionQuery.refresh()
      if (transactionQuery.status.value === 'stale') {
        toast.error('已刪除，但交易列表重新整理失敗')
      }
    } catch (e) {
      toast.error(e instanceof ApiError ? e.userMessage : '刪除失敗')
    }
  }
}

// 以全域財務格式化規則顯示交易金額。
function formatAmount(amount: number) {
  return formatMoney(amount)
}

// 載入支付方式選項並保留可供表單重試的失敗狀態。
async function fetchPaymentMethods() {
  paymentMethodsLoading.value = true
  paymentMethodsError.value = null
  try {
    const result = await api.paymentMethods.list({ pageSize: 999 })
    paymentMethods.value = result.items
  } catch (error) {
    paymentMethodsError.value = error instanceof ApiError ? error.userMessage : '支付方式資料載入失敗，請重試。'
  } finally {
    paymentMethodsLoading.value = false
  }
}

// 載入信用卡選項並保留分期表單可呈現的錯誤狀態。
async function fetchCreditCards() {
  creditCardsLoading.value = true
  creditCardsError.value = null
  try {
    const result = await api.creditCards.list({ pageSize: 999 })
    creditCards.value = result.items
  } catch (error) {
    creditCardsError.value = error instanceof ApiError ? error.userMessage : '信用卡資料載入失敗，請重試。'
  } finally {
    creditCardsLoading.value = false
  }
}

// 重試交易表單所需的所有參考資料查詢。
async function retryReferenceData() {
  await Promise.all([fetchCategories(), fetchPaymentMethods(), fetchCreditCards()])
}

// 送出期間拒絕外部關閉事件，避免遺失正在處理的交易狀態。
function handleModalOpenChange(value: boolean) {
  if (!value && saving.value) return
  modalOpen.value = value
}

// 讓使用者在結果不確定時主動重新整理交易列表以檢查伺服器狀態。
async function refreshTransactionList() {
  await transactionQuery.refresh()
}

onMounted(() => {
  void retryReferenceData()
})
</script>

<template>
  <div class="p-4 lg:p-6">
    <div class="flex items-center justify-between mb-6">
      <div>
        <h1 class="text-2xl font-bold text-text-primary">交易明細</h1>
        <p class="text-xs text-text-secondary mt-1">所有交易記錄 · Transactions</p>
      </div>
      <Button @click="openCreate">+ {{ activeTab === 'all' ? '新增' : activeTab === 'Income' ? '新增收入' : '新增支出' }}</Button>
    </div>

    <div class="flex gap-1 mb-6 bg-bg-raised rounded-lg p-1 w-fit">
      <button
        v-for="tab in ([{ key: 'all', label: '全部' }, { key: 'Income', label: '收入' }, { key: 'Expense', label: '支出' }] as const)"
        :key="tab.key"
        class="px-4 py-1.5 text-sm rounded-md transition-colors cursor-pointer"
        :class="activeTab === tab.key ? 'bg-bg-active text-text-primary shadow-sm' : 'text-text-secondary hover:text-text-primary'"
        @click="activeTab = tab.key"
      >
        {{ tab.label }}
      </button>
    </div>

    <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4 mb-6">
      <template v-if="activeTab === 'all'">
        <Card>
          <div class="flex items-center gap-4">
            <div class="w-11 h-11 rounded-xl bg-color-info flex items-center justify-center">
              <Icon name="receipt" :size="22" class="text-text-on-accent" />
            </div>
            <div>
              <p class="text-xs text-text-secondary">總金額</p>
              <p class="text-xl font-bold text-text-primary">{{ formatSummaryValue(stats.total) }}</p>
            </div>
          </div>
        </Card>
        <Card>
          <div class="flex items-center gap-4">
            <div class="w-11 h-11 rounded-xl bg-color-income flex items-center justify-center">
              <Icon name="arrow-up" :size="22" class="text-text-on-accent" />
            </div>
            <div>
              <p class="text-xs text-text-secondary">總收入</p>
              <p class="text-xl font-bold text-color-income-text">{{ formatSummaryValue(stats.income) }}</p>
            </div>
          </div>
        </Card>
        <Card>
          <div class="flex items-center gap-4">
            <div class="w-11 h-11 rounded-xl bg-color-expense-action flex items-center justify-center">
              <Icon name="arrow-down" :size="22" class="text-color-expense-action-text" />
            </div>
            <div>
              <p class="text-xs text-text-secondary">總支出</p>
              <p class="text-xl font-bold text-color-expense-text">{{ formatSummaryValue(stats.expense) }}</p>
            </div>
          </div>
        </Card>
        <Card>
          <div class="flex items-center gap-4">
            <div class="w-11 h-11 rounded-xl bg-color-warning flex items-center justify-center">
              <Icon name="shopping-bag" :size="22" class="text-text-on-accent" />
            </div>
            <div>
              <p class="text-xs text-text-secondary">筆數</p>
              <p class="text-xl font-bold text-text-primary">{{ stats.count }} 筆</p>
            </div>
          </div>
        </Card>
      </template>
      <template v-else>
        <Card>
          <div class="flex items-center gap-4">
            <div class="w-11 h-11 rounded-xl bg-color-info flex items-center justify-center">
              <Icon name="receipt" :size="22" class="text-text-on-accent" />
            </div>
            <div>
              <p class="text-xs text-text-secondary">{{ activeTab === 'Income' ? '總收入' : '總支出' }}</p>
              <p class="text-xl font-bold text-text-primary">{{ formatSummaryValue(stats.total) }}</p>
            </div>
          </div>
        </Card>
        <Card>
          <div class="flex items-center gap-4">
            <div class="w-11 h-11 rounded-xl bg-color-warning flex items-center justify-center">
              <Icon name="calendar" :size="22" class="text-text-on-accent" />
            </div>
            <div>
              <p class="text-xs text-text-secondary">{{ activeTab === 'Income' ? '日均收入' : '日均支出' }}</p>
              <p class="text-xl font-bold text-text-primary">{{ formatSummaryValue(stats.dailyAvg) }}</p>
            </div>
          </div>
        </Card>
        <Card>
          <div class="flex items-center gap-4">
            <div class="w-11 h-11 rounded-xl bg-color-income flex items-center justify-center">
              <Icon name="shopping-bag" :size="22" class="text-text-on-accent" />
            </div>
            <div>
              <p class="text-xs text-text-secondary">{{ activeTab === 'Income' ? '收入筆數' : '支出筆數' }}</p>
              <p class="text-xl font-bold text-text-primary">{{ stats.count }} 筆</p>
            </div>
          </div>
        </Card>
        <Card>
          <div class="flex items-center gap-4">
            <div class="w-11 h-11 rounded-xl bg-color-expense-action flex items-center justify-center">
              <Icon name="arrow-up" :size="22" class="text-color-expense-action-text" />
            </div>
            <div>
              <p class="text-xs text-text-secondary">單筆最高</p>
              <p class="text-xl font-bold text-text-primary">{{ formatSummaryValue(stats.max) }}</p>
            </div>
          </div>
        </Card>
      </template>
    </div>

    <Card>
      <div class="flex flex-wrap items-center gap-3 mb-4">
        <span class="text-sm font-medium text-text-primary">日期</span>
        <input
          v-model="startDate"
          type="date"
          @change="validateDateRange"
           class="px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
        />
        <span class="text-text-secondary">~</span>
        <input
          v-model="endDate"
          type="date"
          @change="validateDateRange"
           class="px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
        />
        <span class="text-xs text-text-tertiary">（最多 1 年）</span>
        <span class="text-sm font-medium text-text-primary ml-2">類別</span>
        <select
          v-model="selectedCategory"
          class="px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
        >
          <option value="">全部</option>
          <option v-for="c in categories.filter(c => activeTab === 'all' || c.type === activeTab)" :key="c.id" :value="c.id">{{ c.name }}</option>
        </select>
        <input
          v-model="search"
          placeholder="搜尋項目或備註..."
           class="px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary min-w-[200px]"
        />
      </div>

      <DataTable
        :columns="columns"
        :loading="loading"
        :items="transactions"
        :error="transactionListError"
        :refreshing="transactionQuery.status.value === 'refreshing'"
        :retry="transactionQuery.retry"
      >
        <template #empty>
          <div class="text-center text-text-tertiary py-4">尚無交易資料</div>
        </template>
        <tr v-for="(item, idx) in transactions" :key="item.id" class="border-b border-border-default hover:bg-bg-raised">
          <td class="py-3 px-4 text-text-secondary text-sm w-[60px]">{{ (pagination.page.value - 1) * pagination.pageSize.value + idx + 1 }}</td>
          <td class="py-3 px-4 text-text-primary text-sm whitespace-nowrap w-[100px]">{{ item.date.slice(0, 10) }}</td>
          <td class="py-3 px-4 w-[120px]">
            <span
              class="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium"
              :style="{
                backgroundColor: item.category?.color ? `${item.category.color}20` : 'var(--color-color-info-bg)',
                color: item.category?.color || 'var(--color-color-info-text)',
              }"
            >
              {{ item.category?.name }}
            </span>
          </td>
          <td class="py-3 px-4 w-[80px]">
            <span
              class="inline-flex items-center px-2 py-0.5 rounded-md text-xs font-medium"
              :class="item.type === 'Income' ? 'bg-color-income-bg text-color-income-text' : 'bg-color-expense-bg text-color-expense-text'"
            >
              {{ item.type === 'Income' ? '收入' : '支出' }}
            </span>
          </td>
          <td class="py-3 px-4 text-text-primary text-sm">{{ item.description }}</td>
          <td class="py-3 px-4 text-right w-[130px]">
            <span :class="item.type === 'Income' ? 'text-color-income-text' : 'text-color-expense-text'" class="font-semibold text-sm">
              {{ formatAmount(item.amount) }}
            </span>
          </td>
          <td class="py-3 px-4 w-[110px]">
            <span
              v-if="item.paymentMethod"
              class="inline-flex items-center px-2 py-0.5 rounded-md text-xs border"
              :style="{
                backgroundColor: item.paymentMethod.color ? `${item.paymentMethod.color}20` : 'var(--color-bg-raised)',
                color: item.paymentMethod.color || 'var(--color-text-tertiary)',
                borderColor: item.paymentMethod.color || 'var(--color-border-strong)',
              }"
            >
              {{ item.paymentMethod.name }}
            </span>
          </td>
          <td class="py-3 px-4 text-text-tertiary text-sm">{{ item.notes }}</td>
          <td class="py-3 px-4 w-[80px]">
            <div class="flex items-center gap-1">
              <button
                class="p-1.5 rounded-lg hover:bg-bg-raised text-text-secondary cursor-pointer transition-colors"
                @click="openEdit(item)"
              >
                <Icon name="pencil" :size="16" />
              </button>
              <button
                class="p-1.5 rounded-lg hover:bg-bg-raised text-color-expense-text cursor-pointer transition-colors"
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

    <Modal
      :open="modalOpen"
      :title="editingItem ? '編輯交易' : '新增交易'"
      description="填寫交易日期、內容與付款方式"
      size="lg"
      mobile-full-screen
      scroll-body
      :close-disabled="saving"
      @update:open="handleModalOpenChange"
    >
      <TransactionForm
        v-if="modalOpen"
        :key="formKey"
        :initial-value="form"
        :categories="categories"
        :payment-methods="paymentMethods"
        :credit-cards="creditCards"
        :editing="editingItem"
        :submitting="saving"
        :reference-data-ready="referenceDataReady"
        :reference-data-error="referenceDataError"
        :credit-card-data-ready="creditCardDataReady"
        :credit-card-data-error="creditCardDataError"
        :submission-error="submissionError"
        :submission-notice="submissionNotice"
        :submission-uncertain="submissionUncertain"
        :submission-retry-allowed="submissionRetryAllowed"
        @submit="save"
        @cancel="modalOpen = false"
        @retry-reference-data="retryReferenceData"
        @refresh-transactions="refreshTransactionList"
      />
    </Modal>

    <ConfirmDialog
      :open="confirmOpen"
      title="刪除交易"
      description="確定要刪除此交易記錄嗎？此操作無法復原。"
      variant="danger"
      @update:open="confirmOpen = $event"
      @confirm="doDelete"
    />
  </div>
</template>
