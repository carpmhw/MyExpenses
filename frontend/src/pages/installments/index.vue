<script setup lang="ts">
import { ref, computed, inject, onMounted, watch } from 'vue'
import { ApiError, api } from '../../api'
import type { Installment, CreditCard, CreditCardBill } from '../../types'
import Card from '../../components/ui/Card.vue'
import Button from '../../components/ui/Button.vue'
import DataTable from '../../components/ui/DataTable.vue'
import Modal from '../../components/ui/Modal.vue'
import ConfirmDialog from '../../components/ui/ConfirmDialog.vue'
import Input from '../../components/ui/Input.vue'
import Select from '../../components/ui/Select.vue'
import Icon from '../../components/ui/Icon.vue'
import QueryState from '../../components/ui/QueryState.vue'
import { formatMoney } from '../../utils/format'
import { usePagination } from '../../composables/usePagination'
import { useTimeZone } from '../../composables/useTimeZone'
import { addCalendarDays, formatDateOnly, getCurrentMonthRange, isDateOnlyBefore } from '../../utils/timezone'
import { createIdempotencyKeyState } from '../../utils/idempotency'
import { useAsyncQuery } from '../../composables/useAsyncQuery'
import { useAsyncMutation } from '../../composables/useAsyncMutation'

const toast = inject<{ success: (m: string) => void; error: (m: string) => void }>('toast')!
const timeZone = useTimeZone()

const creditCards = ref<CreditCard[]>([])
const creditCardLoading = ref(false)
const creditCardError = ref<string | null>(null)
const pagination = usePagination(1, 15)

const filterCardId = ref<number | ''>('')
const filterStatus = ref<string>('')

// 回傳設定時區中的信用卡交易預設起始日期。
function getDefaultStartDate(): string {
  return getCurrentMonthRange(new Date(), timeZone.timeZoneId.value).start
}
// 回傳設定時區中的信用卡交易預設結束日期。
function getDefaultEndDate(): string {
  return getCurrentMonthRange(new Date(), timeZone.timeZoneId.value).end
}

const startDate = ref(getDefaultStartDate())
const endDate = ref(getDefaultEndDate())

const installmentListQuery = useAsyncQuery({
  key: () => [
    'installments',
    pagination.page.value,
    pagination.pageSize.value,
    filterCardId.value,
    filterStatus.value,
    startDate.value,
    endDate.value,
  ],
  query: ({ signal }) => api.installments.list({
    page: pagination.page.value,
    pageSize: pagination.pageSize.value,
    cardId: filterCardId.value || undefined,
    dateStart: startDate.value || undefined,
    dateEnd: endDate.value || undefined,
    status: filterStatus.value || undefined,
  }, { signal }),
  isEmpty: result => result.items.length === 0,
})
const billQuery = useAsyncQuery<CreditCardBill[]>({
  key: () => ['unpaid-bills', filterCardId.value],
  query: ({ signal }) => api.creditCardBills.list({ isPaid: false, cardId: filterCardId.value || undefined }, { signal }),
  isEmpty: result => result.length === 0,
})
const scheduleInstallmentId = ref<number | null>(null)
const scheduleQuery = useAsyncQuery<Installment | null>({
  key: () => ['installment-detail', scheduleInstallmentId.value],
  query: ({ signal }) => scheduleInstallmentId.value === null
    ? Promise.resolve(null)
    : api.installments.get(scheduleInstallmentId.value, { signal }),
  isEmpty: result => result === null,
})

const installments = computed(() => installmentListQuery.data.value?.items ?? [])
const summary = computed(() => installmentListQuery.data.value?.summary ?? null)
const unpaidBills = computed(() => billQuery.data.value ?? [])
const scheduleInstallment = computed(() => scheduleQuery.data.value)
const loading = computed(() => installmentListQuery.status.value === 'loading' || installmentListQuery.status.value === 'refreshing')
const saving = computed(() => mutation.status.value === 'submitting' || deleteMutation.status.value === 'submitting' || paymentMutation.status.value === 'submitting')

// 在日期範圍成為查詢識別前驗證其上下限。
function validateDateRange(): void {
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
const editingItem = ref<Installment | null>(null)
const MAX_INSTALLMENT_PERIODS = 60
const form = ref({
  transactionId: null as number | null,
  cardId: null as number | null,
  totalAmount: 0,
  periods: 3,
  perPeriod: 0,
  purchaseDate: timeZone.getToday(),
  description: '',
})

const scheduleOpen = ref(false)

const confirmOpen = ref(false)
const deletingId = ref<number | null>(null)

const paymentConfirmOpen = ref(false)
const payingPaymentId = ref<number | null>(null)
const markingAsPaid = ref(true)
const paidDate = ref(timeZone.getToday())
const standaloneInstallmentIdempotency = createIdempotencyKeyState()

type InstallmentMutationInput =
  | { operation: 'create'; data: Parameters<typeof api.installments.create>[0]; idempotencyKey: string }
  | { operation: 'update'; id: number; data: Parameters<typeof api.installments.update>[1] }

const mutation = useAsyncMutation<InstallmentMutationInput, unknown>({
  mutate: input => input.operation === 'create'
    ? api.installments.create(input.data, input.idempotencyKey)
    : api.installments.update(input.id, input.data),
  classifyError: error => ({ uncertain: !(error instanceof ApiError && [400, 404, 409, 422].includes(error.status ?? 0)) }),
})
const deleteMutation = useAsyncMutation<number, void>({
  mutate: id => api.installments.delete(id),
  classifyError: error => ({ uncertain: !(error instanceof ApiError && [400, 404, 409, 422].includes(error.status ?? 0)) }),
})
const paymentMutation = useAsyncMutation<
  { id: number; paymentId: number; data: { isPaid: boolean; paidDate?: string } },
  ReturnType<typeof api.installments.markPayment> extends Promise<infer T> ? T : never
>({
  mutate: input => api.installments.markPayment(input.id, input.paymentId, input.data),
  classifyError: error => ({ uncertain: !(error instanceof ApiError && [400, 404, 409, 422].includes(error.status ?? 0)) }),
})

const columns = [
  { key: 'seq', label: '序號' },
  { key: 'purchaseDate', label: '刷卡日期' },
  { key: 'description', label: '描述' },
  { key: 'card', label: '信用卡' },
  { key: 'totalAmount', label: '總金額', align: 'right' as const },
  { key: 'periods', label: '期數' },
  { key: 'perPeriod', label: '每期金額', align: 'right' as const },
  { key: 'remaining', label: '剩餘期數' },
  { key: 'status', label: '狀態' },
  { key: 'progress', label: '進度' },
]

const cardOptions = computed(() =>
  creditCards.value.map(c => ({
    value: c.id,
    label: `${c.bankName} (${c.lastFourDigits})`,
  }))
)

const stats = computed(() => {
  if (!summary.value) return { total: null, active: null, monthlyDue: null }
  return {
    total: summary.value.totalCount,
    active: summary.value.activeCount,
    monthlyDue: summary.value.dueAmount,
  }
})

// 格式化可為空的信用卡交易摘要，避免把查詢失敗誤呈現為零。
function formatSummaryValue(value: number | null): string {
  return value === null ? '—' : formatMoney(value)
}

// 將列表或明細錯誤轉換為安全的行內訊息。
function queryErrorMessage(error: unknown): string {
  return error instanceof ApiError ? error.userMessage : '信用卡交易資料載入失敗，請重試。'
}

const hasPaidPayments = computed(() =>
  editingItem.value?.payments?.some(p => p.isPaid) ?? false
)

const formErrors = computed(() => {
  const errs: Record<string, string> = {}
  if (!form.value.totalAmount || form.value.totalAmount <= 0) errs.totalAmount = '總金額必須大於零'
  if (!Number.isInteger(form.value.periods) || form.value.periods < 1 || form.value.periods > MAX_INSTALLMENT_PERIODS) {
    errs.periods = '期數必須為 1 至 60 期'
  }
  if (!form.value.cardId) errs.cardId = '請選擇信用卡'
  if (!form.value.purchaseDate) errs.purchaseDate = '請選擇刷卡日期'
  if (!form.value.description?.trim()) errs.description = '請填寫交易描述'
  return errs
})

interface SchedulePreviewPayment {
  period: number
  amount: number
  dueDate: string
}

// 依後端相同的拆分規則計算信用卡交易付款預覽金額。
function calculatePreviewAmounts(totalAmount: number, periods: number): number[] {
  if (!Number.isFinite(totalAmount) || totalAmount <= 0 || !Number.isInteger(periods) || periods < 1 || periods > MAX_INSTALLMENT_PERIODS) return []
  const perPeriod = Math.floor(totalAmount / periods)
  const remainder = totalAmount - perPeriod * periods
  return Array.from({ length: periods }, (_, index) => index === periods - 1 ? perPeriod + remainder : perPeriod)
}

// 依信用卡結帳週期計算付款預覽到期日。
function calculatePreviewDueDate(purchaseDate: string, selectedCard: CreditCard, period: number): string {
  const [year, month, day] = purchaseDate.slice(0, 10).split('-').map(Number)
  const monthIndex = month - 1 + (day > selectedCard.statementDay ? 1 : 0) + period - 1
  const targetYear = year + Math.floor(monthIndex / 12)
  const targetMonth = (monthIndex % 12) + 1
  const targetDay = Math.min(selectedCard.dueDay, new Date(targetYear, targetMonth, 0).getDate())
  return `${targetYear}-${String(targetMonth).padStart(2, '0')}-${String(targetDay).padStart(2, '0')}`
}

// 將一期交易顯示為一次付清，其餘期數保留一般期數文字。
function formatPeriodLabel(periods: number): string {
  return periods === 1 ? '1 期（一次付清）' : `${periods} 期`
}

const schedulePreview = computed<SchedulePreviewPayment[]>(() => {
  const selectedCard = creditCards.value.find(card => card.id === form.value.cardId)
  if (!selectedCard || !form.value.purchaseDate) return []
  return calculatePreviewAmounts(form.value.totalAmount, form.value.periods).map((amount, index) => ({
    period: index + 1,
    amount,
    dueDate: calculatePreviewDueDate(form.value.purchaseDate, selectedCard, index + 1),
  }))
})

watch([() => form.value.totalAmount, () => form.value.periods], () => {
  if (form.value.totalAmount > 0 && Number.isInteger(form.value.periods) && form.value.periods > 0 && form.value.periods <= MAX_INSTALLMENT_PERIODS) {
    form.value.perPeriod = Math.floor(form.value.totalAmount / form.value.periods)
  } else {
    form.value.perPeriod = 0
  }
})

watch(() => installmentListQuery.data.value?.total, total => {
  if (total !== undefined) pagination.total.value = total
})

watch([filterCardId, filterStatus, startDate, endDate], () => {
  pagination.reset()
})

// 載入信用卡交易篩選與表單使用的信用卡選項。
async function fetchCreditCards(): Promise<void> {
  creditCardLoading.value = true
  creditCardError.value = null
  try {
    const result = await api.creditCards.list({ pageSize: 999 })
    creditCards.value = result.items
  } catch (error) {
    creditCardError.value = error instanceof ApiError ? error.userMessage : '信用卡選項載入失敗，請重試。'
  } finally {
    creditCardLoading.value = false
  }
}

// 重設 standalone 信用卡交易表單並開始新的邏輯送出。
function openCreate(): void {
  standaloneInstallmentIdempotency.clear()
  editingItem.value = null
  form.value = {
    transactionId: null,
    cardId: null,
    totalAmount: 0,
    periods: 3,
    perPeriod: 0,
    purchaseDate: timeZone.getToday(),
    description: '',
  }
  modalOpen.value = true
}

// 關閉信用卡交易表單時捨棄本次開啟期間的冪等命令狀態。
function handleModalOpenChange(open: boolean): void {
  modalOpen.value = open
  if (!open) standaloneInstallmentIdempotency.clear()
}

// 將伺服器信用卡交易載入編輯表單，不先進行 optimistic 變更。
function openEdit(item: Installment): void {
  editingItem.value = item
  form.value = {
    transactionId: item.transactionId,
    cardId: item.cardId,
    totalAmount: item.totalAmount,
    periods: item.periods,
    perPeriod: item.perPeriod,
    purchaseDate: item.purchaseDate?.slice(0, 10) || timeZone.getToday(),
    description: item.description || '',
  }
  modalOpen.value = true
}

// 以單一冪等命令建立或原子更新信用卡交易付款時程。
async function save(): Promise<void> {
  const errs = formErrors.value
  if (Object.keys(errs).length > 0) return

  let mutationSucceeded = false
  try {
    if (editingItem.value) {
      await mutation.submit({ operation: 'update', id: editingItem.value.id, data: {
        cardId: form.value.cardId,
        totalAmount: form.value.totalAmount,
        periods: form.value.periods,
        purchaseDate: form.value.purchaseDate,
        description: form.value.description,
      } })
      toast.success('信用卡交易已更新')
    } else {
      const createRequest = {
        transactionId: form.value.transactionId,
        cardId: form.value.cardId,
        totalAmount: form.value.totalAmount,
        periods: form.value.periods,
        purchaseDate: form.value.purchaseDate,
        description: form.value.description,
      }
      const idempotencyKey = standaloneInstallmentIdempotency.prepare(createRequest)
      await mutation.submit({ operation: 'create', data: createRequest, idempotencyKey })
      standaloneInstallmentIdempotency.clear()
      toast.success('信用卡交易已建立')
    }
    modalOpen.value = false
    mutationSucceeded = true
  } catch (e) {
      toast.error(e instanceof ApiError ? e.userMessage : '信用卡交易儲存失敗')
  }

  if (mutationSucceeded) {
    await installmentListQuery.refresh()
    if (installmentListQuery.status.value === 'stale') {
      toast.error('已儲存，但信用卡交易列表重新整理失敗')
    }
  }
}

// 開啟指定信用卡交易的刪除確認視窗。
function confirmDelete(id: number): void {
  deletingId.value = id
  confirmOpen.value = true
}

// 確認刪除信用卡交易後重新整理目前查詢。
async function doDelete(): Promise<void> {
  if (deletingId.value !== null) {
    const id = deletingId.value
    confirmOpen.value = false
    deletingId.value = null

    try {
      await deleteMutation.submit(id)
       toast.success('信用卡交易已刪除')
      await installmentListQuery.refresh()
      if (installmentListQuery.status.value === 'stale') {
         toast.error('已刪除，但信用卡交易列表重新整理失敗')
      }
    } catch (e) {
      toast.error(e instanceof ApiError ? e.userMessage : '刪除失敗')
    }
  }
}

// 選取信用卡交易並開啟獨立擁有的付款時程查詢。
function openSchedule(item: Installment): void {
  scheduleInstallmentId.value = item.id
  scheduleOpen.value = true
}

// 以明確目標狀態開啟付款確認視窗，而不是依賴 toggle。
function confirmMarkPayment(paymentId: number, isPaid: boolean): void {
  payingPaymentId.value = paymentId
  markingAsPaid.value = !isPaid
  paidDate.value = timeZone.getToday()
  paymentConfirmOpen.value = true
}

// 套用確認後的已繳或未繳狀態並重新整理衍生信用卡交易資料。
async function doMarkPayment(): Promise<void> {
  if (!scheduleInstallment.value || payingPaymentId.value === null) return

  const id = scheduleInstallment.value.id
  const paymentId = payingPaymentId.value

  if (markingAsPaid.value && !paidDate.value) {
    toast.error('請選擇實際繳款日')
    return
  }

  try {
    const paymentRequest = markingAsPaid.value
      ? { isPaid: true, paidDate: paidDate.value }
      : { isPaid: false }
    const updated = await paymentMutation.submit({ id, paymentId, data: paymentRequest })
    paymentConfirmOpen.value = false
    payingPaymentId.value = null
    toast.success(markingAsPaid.value ? '已標記為已繳款' : '已取消繳款標記')
    if (scheduleQuery.data.value) {
      scheduleQuery.data.value = {
        ...scheduleQuery.data.value,
        remainingPeriods: updated.remainingPeriods,
        status: updated.status,
        payments: updated.payments,
      }
    }
    await installmentListQuery.refresh()
    if (installmentListQuery.status.value === 'stale') {
       toast.error('付款狀態已更新，但信用卡交易列表重新整理失敗')
    }
  } catch (e) {
     toast.error(e instanceof ApiError ? e.userMessage : '信用卡付款狀態更新失敗')
  }
}

// 格式化信用卡交易列表中的信用卡名稱。
function getCardDisplay(inst: Installment): string {
  if (inst.card) return `${inst.card.bankName} (${inst.card.lastFourDigits})`
  return '-'
}

// 格式化可選日期，不自行捏造缺少的日期。
function formatDate(dateStr: string | undefined | null): string {
  if (!dateStr) return '-'
  return formatDateOnly(dateStr)
}

// 計算信用卡交易已繳期數的進度百分比。
function progressPercent(inst: Installment): number {
  if (inst.periods === 0) return 0
  return Math.round(((inst.periods - inst.remainingPeriods) / inst.periods) * 100)
}

onMounted(async () => {
  await fetchCreditCards()
})
</script>

<template>
  <div class="p-4 lg:p-6">
    <div class="flex items-center justify-between mb-6">
      <div>
        <h1 class="text-2xl font-bold text-text-primary">信用卡交易</h1>
        <p class="text-xs text-text-secondary mt-1">刷卡消費與付款管理 · Credit Card Transactions</p>
      </div>
      <Button @click="openCreate">+ 新增交易</Button>
    </div>

    <div class="grid grid-cols-1 sm:grid-cols-3 gap-4 mb-6">
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl bg-color-info flex items-center justify-center">
            <Icon name="receipt" :size="22" class="text-text-on-accent" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">總交易筆數</p>
            <p class="text-xl font-bold text-text-primary">{{ stats.total ?? '—' }} 筆</p>
          </div>
        </div>
      </Card>
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl bg-color-warning flex items-center justify-center">
            <Icon name="clock" :size="22" class="text-text-on-accent" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">進行中</p>
            <p class="text-xl font-bold text-text-primary">{{ stats.active ?? '—' }} 筆</p>
          </div>
        </div>
      </Card>
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl bg-color-credit flex items-center justify-center">
            <Icon name="credit-card" :size="22" class="text-text-on-accent" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">本月應繳總額</p>
            <p class="text-xl font-bold text-color-credit-text">{{ formatSummaryValue(stats.monthlyDue) }}</p>
          </div>
        </div>
      </Card>
    </div>

    <div v-if="billQuery.status.value === 'loading'" class="mb-6">
      <Card><QueryState :status="billQuery.status.value" /></Card>
    </div>
    <div v-else-if="billQuery.status.value === 'error'" class="mb-6">
      <Card>
        <QueryState
          :status="billQuery.status.value"
          :error-message="queryErrorMessage(billQuery.error.value)"
          :retry="billQuery.retry"
        />
      </Card>
    </div>
    <div v-else-if="unpaidBills.length > 0" class="mb-6">
      <Card>
        <div class="flex items-center justify-between mb-3">
          <h2 class="text-sm font-semibold text-text-primary">未繳帳單</h2>
          <span class="text-xs text-text-secondary">{{ unpaidBills.length }} 筆</span>
        </div>
        <table class="w-full text-sm">
          <thead>
            <tr class="border-b border-border-default">
              <th class="text-left py-2 pr-2 text-text-secondary font-medium">信用卡</th>
              <th class="text-left py-2 pr-2 text-text-secondary font-medium">帳單月份</th>
              <th class="text-right py-2 pr-2 text-text-secondary font-medium">應繳金額</th>
              <th class="text-left py-2 pr-2 text-text-secondary font-medium">繳款截止日</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="bill in unpaidBills" :key="bill.id" class="border-b border-border-default last:border-b-0">
              <td class="py-2 pr-2 text-text-primary">{{ bill.card?.bankName }} ({{ bill.card?.lastFourDigits }})</td>
              <td class="py-2 pr-2 text-text-primary">{{ bill.period }}</td>
              <td class="py-2 pr-2 text-right text-text-primary font-medium">{{ formatMoney(bill.totalAmount) }}</td>
              <td class="py-2 pr-2" :class="isDateOnlyBefore(bill.dueDate, timeZone.getToday()) ? 'text-color-expense-text font-medium' : 'text-text-primary'">
                {{ formatDate(bill.dueDate) }}
              </td>
            </tr>
          </tbody>
        </table>
        <div class="flex justify-end pt-2 mt-2 border-t border-border-default">
          <span class="text-sm text-text-secondary">
            未繳總額 <strong class="text-text-primary">{{ formatMoney(unpaidBills.reduce((sum, b) => sum + b.totalAmount, 0)) }}</strong>
          </span>
        </div>
      </Card>
    </div>

    <div v-else class="mb-6">
      <Card>
        <div class="flex items-center gap-2 text-sm text-color-income-text">
          <Icon name="check-circle" :size="18" />
          <span>目前無未繳帳單</span>
        </div>
      </Card>
    </div>

    <div v-if="creditCardLoading || creditCardError" class="mb-6">
      <Card>
        <div v-if="creditCardLoading" role="status" aria-live="polite" class="py-2 text-center text-sm text-text-tertiary">
          正在載入信用卡選項...
        </div>
        <div v-else role="alert" class="flex items-center justify-between gap-3 text-sm text-color-warning-text">
          <span>{{ creditCardError }}</span>
          <Button type="button" variant="ghost" @click="fetchCreditCards">重試信用卡選項</Button>
        </div>
      </Card>
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
        <span class="text-sm font-medium text-text-primary ml-2">信用卡</span>
        <select
          v-model="filterCardId"
          :disabled="creditCardLoading || Boolean(creditCardError)"
          class="px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
        >
          <option value="">全部</option>
          <option v-for="c in creditCards" :key="c.id" :value="c.id">{{ c.bankName }} ({{ c.lastFourDigits }})</option>
        </select>
        <span class="text-sm font-medium text-text-primary ml-2">狀態</span>
        <select
          v-model="filterStatus"
          class="px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
        >
          <option value="">全部</option>
          <option value="Active">進行中</option>
          <option value="PaidOff">已結清</option>
        </select>
      </div>
      <DataTable
        :columns="columns"
        :loading="loading"
        :items="installments"
        :error="installmentListQuery.status.value === 'error' || installmentListQuery.status.value === 'stale' ? queryErrorMessage(installmentListQuery.error.value) : null"
        :refreshing="installmentListQuery.status.value === 'refreshing'"
        :retry="installmentListQuery.retry"
      >
        <template #empty>
          <div class="text-center text-text-tertiary py-4">尚無信用卡交易資料</div>
        </template>
        <tr v-for="(item, idx) in installments" :key="item.id" class="border-b border-border-default hover:bg-bg-raised">
          <td class="py-3 px-4 text-text-secondary text-sm w-[60px]">{{ (pagination.page.value - 1) * pagination.pageSize.value + idx + 1 }}</td>
          <td class="py-3 px-4 text-text-primary text-sm whitespace-nowrap w-[100px]">{{ formatDate(item.purchaseDate) }}</td>
          <td class="py-3 px-4 text-text-primary text-sm">{{ item.description }}</td>
          <td class="py-3 px-4 text-text-primary text-sm">{{ getCardDisplay(item) }}</td>
          <td class="py-3 px-4 text-text-primary font-bold text-sm w-[130px] text-right">{{ formatMoney(item.totalAmount) }}</td>
           <td class="py-3 px-4 text-text-primary text-sm w-[140px]">{{ formatPeriodLabel(item.periods) }}</td>
          <td class="py-3 px-4 text-text-primary text-sm w-[120px] text-right">{{ formatMoney(item.perPeriod) }}</td>
          <td class="py-3 px-4 text-text-primary text-sm w-[90px]">{{ item.remainingPeriods }}</td>
          <td class="py-3 px-4 w-[90px]">
            <span
              class="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium"
              :class="item.status === 'PaidOff'
                 ? 'bg-color-income-bg text-color-income-text'
                 : 'bg-color-info-bg text-color-info-text'"
            >
              {{ item.status === 'PaidOff' ? '已結清' : '進行中' }}
            </span>
          </td>
          <td class="py-3 px-4 w-[130px]">
            <div class="flex items-center gap-2">
              <div class="flex-1 h-2 bg-bg-raised rounded-full overflow-hidden">
                <div
                  class="h-full rounded-full transition-all"
                  :class="progressPercent(item) >= 100 ? 'bg-color-income' : 'bg-color-info'"
                  :style="{ width: `${progressPercent(item)}%` }"
                />
              </div>
              <span class="text-xs text-text-secondary w-[40px]">{{ progressPercent(item) }}%</span>
            </div>
          </td>
          <td class="py-3 px-4 w-[120px]">
            <div class="flex items-center gap-1">
              <button
                class="p-1.5 rounded-lg hover:bg-bg-raised text-text-secondary cursor-pointer transition-colors"
                title="檢視時程"
                @click="openSchedule(item)"
              >
                <Icon name="calendar" :size="16" />
              </button>
              <button
                class="p-1.5 rounded-lg hover:bg-bg-raised text-text-secondary cursor-pointer transition-colors"
                title="編輯"
                @click="openEdit(item)"
              >
                <Icon name="pencil" :size="16" />
              </button>
              <button
                class="p-1.5 rounded-lg hover:bg-bg-raised text-color-expense-text cursor-pointer transition-colors"
                title="刪除"
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

    <Modal :open="modalOpen" :title="editingItem ? '編輯信用卡交易' : '新增信用卡交易'" @update:open="handleModalOpenChange">
      <form class="space-y-4" @submit.prevent="save">
        <div>
          <label for="credit-card-transaction-description" class="block text-sm font-medium text-text-primary mb-1">交易描述</label>
          <Input id="credit-card-transaction-description" v-model="form.description" :error="formErrors.description" />
        </div>
        <div class="grid grid-cols-2 gap-4">
          <div>
            <label for="credit-card-transaction-total-amount" class="block text-sm font-medium text-text-primary mb-1">總金額</label>
            <Input
              id="credit-card-transaction-total-amount"
              :model-value="form.totalAmount || ''"
              type="number"
              step="0.01"
              :disabled="hasPaidPayments"
              :error="formErrors.totalAmount"
              @update:model-value="form.totalAmount = Number($event) || 0"
            />
            <p v-if="hasPaidPayments" class="mt-1 text-xs text-color-warning-text">已有繳款記錄，不可修改</p>
          </div>
          <div>
            <label for="credit-card-transaction-periods" class="block text-sm font-medium text-text-primary mb-1">期數</label>
            <Input
              id="credit-card-transaction-periods"
              :model-value="form.periods"
              type="number"
              step="1"
              :min="1"
              :max="MAX_INSTALLMENT_PERIODS"
              :disabled="hasPaidPayments"
              :error="formErrors.periods"
              @update:model-value="form.periods = Number.isFinite(Number($event)) ? Number($event) : 0"
            />
            <p v-if="!hasPaidPayments && !formErrors.periods" class="mt-1 text-xs text-text-tertiary">期數必須為 1 至 60 期</p>
            <p v-if="hasPaidPayments" class="mt-1 text-xs text-color-warning-text">已有繳款記錄，不可修改</p>
          </div>
        </div>
        <div>
          <label for="credit-card-transaction-card" class="block text-sm font-medium text-text-primary mb-1">信用卡</label>
          <Select
            id="credit-card-transaction-card"
            :model-value="form.cardId ?? ''"
            :options="cardOptions"
            placeholder="選擇信用卡"
            :disabled="hasPaidPayments || creditCardLoading || Boolean(creditCardError)"
            @update:model-value="form.cardId = Number($event) || null"
          />
          <p v-if="hasPaidPayments" class="mt-1 text-xs text-color-warning-text">已有繳款記錄，不可修改</p>
          <p v-else-if="formErrors.cardId" class="mt-1 text-xs text-color-expense-text">{{ formErrors.cardId }}</p>
        </div>
        <div>
          <label for="credit-card-transaction-date" class="block text-sm font-medium text-text-primary mb-1">刷卡日期</label>
          <input
            id="credit-card-transaction-date"
            v-model="form.purchaseDate"
            type="date"
            :disabled="hasPaidPayments"
            class="w-full px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary disabled:opacity-60 disabled:cursor-not-allowed"
          />
          <p v-if="hasPaidPayments" class="mt-1 text-xs text-color-warning-text">已有繳款記錄，不可修改</p>
          <p v-else-if="formErrors.purchaseDate" class="mt-1 text-xs text-color-expense-text">{{ formErrors.purchaseDate }}</p>
        </div>
        <div v-if="schedulePreview.length > 0" data-testid="credit-card-transaction-schedule-preview" class="rounded-lg border border-border-default bg-bg-raised p-3">
          <div class="flex items-center justify-between gap-3 mb-2">
            <h3 class="text-sm font-semibold text-text-primary">付款時程預覽</h3>
            <span class="text-xs text-text-secondary">{{ formatPeriodLabel(form.periods) }}</span>
          </div>
          <div class="space-y-2 text-xs">
            <div v-for="payment in schedulePreview" :key="payment.period" class="flex items-center justify-between gap-3 text-text-secondary">
              <span>第 {{ payment.period }} 期 · {{ formatDate(payment.dueDate) }}</span>
              <span class="font-medium text-text-primary">{{ formatMoney(payment.amount) }}</span>
            </div>
          </div>
        </div>
        <div class="flex justify-end gap-3 pt-2">
          <Button variant="ghost" type="button" @click="handleModalOpenChange(false)">取消</Button>
          <Button type="submit" :loading="saving">儲存</Button>
        </div>
      </form>
    </Modal>

    <Modal :open="scheduleOpen" title="付款時程" size="lg" @update:open="scheduleOpen = $event">
      <QueryState
        :status="scheduleQuery.status.value"
        :error-message="queryErrorMessage(scheduleQuery.error.value)"
        :retry="scheduleQuery.retry"
      >
      <div v-if="scheduleInstallment" class="space-y-4">
        <div class="flex items-center justify-between text-sm">
          <span class="text-text-secondary">
            {{ scheduleInstallment.description }} · {{ formatPeriodLabel(scheduleInstallment.periods) }}
          </span>
          <span class="font-medium text-text-primary">{{ formatMoney(scheduleInstallment.totalAmount) }}</span>
        </div>
        <table class="w-full text-sm">
          <thead>
            <tr class="border-b border-border-default">
              <th class="text-left py-2 px-2 text-text-secondary font-medium">期數</th>
              <th class="text-right py-2 px-2 text-text-secondary font-medium">應繳金額</th>
              <th class="text-left py-2 px-2 text-text-secondary font-medium">預計繳款截止日</th>
              <th class="text-left py-2 px-2 text-text-secondary font-medium">實際繳款日</th>
              <th class="text-center py-2 px-2 text-text-secondary font-medium">狀態</th>
              <th class="py-2 px-2 w-[60px]"></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="p in scheduleInstallment.payments" :key="p.id" class="border-b border-border-default">
              <td class="py-2 px-2 text-text-primary">第 {{ p.period }} 期</td>
              <td class="py-2 px-2 text-right text-text-primary font-medium">{{ formatMoney(p.amount) }}</td>
              <td class="py-2 px-2 text-text-primary">{{ p.dueDate ? formatDate(p.dueDate) : '-' }}</td>
              <td class="py-2 px-2 text-text-primary">{{ p.paidDate ? formatDate(p.paidDate) : '-' }}</td>
              <td class="py-2 px-2 text-center">
                <span
                  class="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium"
                  :class="p.isPaid
                     ? 'bg-color-income-bg text-color-income-text'
                     : 'bg-bg-raised text-text-secondary'"
                >
                  {{ p.isPaid ? '已繳' : '未繳' }}
                </span>
              </td>
              <td class="py-2 px-2 text-center">
                <button
                  v-if="!p.isPaid"
                  class="px-2 py-1 rounded text-xs font-medium bg-color-income-bg text-color-income-text hover:bg-bg-raised cursor-pointer transition-colors"
                  @click="confirmMarkPayment(p.id, false)"
                >
                  標記已繳
                </button>
                <button
                  v-else
                  class="px-2 py-1 rounded text-xs font-medium bg-bg-raised text-text-secondary hover:bg-bg-active cursor-pointer transition-colors"
                  @click="confirmMarkPayment(p.id, true)"
                >
                  取消已繳
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      </QueryState>
    </Modal>

    <ConfirmDialog
      :open="confirmOpen"
       title="刪除信用卡交易"
       description="確定要刪除此信用卡交易嗎？相關的付款記錄也將一併刪除。"
      variant="danger"
      @update:open="confirmOpen = $event"
      @confirm="doDelete"
    />

    <Modal :open="paymentConfirmOpen" :title="markingAsPaid ? '標記已繳款' : '取消已繳款'" size="sm" @update:open="paymentConfirmOpen = $event">
      <p class="text-sm text-text-secondary mb-4">{{ markingAsPaid ? '確定要將此期標記為已繳款？' : '確定要取消此期的已繳款標記？' }}</p>
      <div v-if="markingAsPaid" class="mb-6">
        <label class="block text-sm font-medium text-text-primary mb-1">實際繳款日</label>
        <input
          v-model="paidDate"
          type="date"
          class="w-full px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
        />
      </div>
      <div class="flex justify-end gap-3">
        <Button variant="ghost" type="button" @click="paymentConfirmOpen = false">取消</Button>
        <Button type="button" :loading="saving" @click="doMarkPayment">確認</Button>
      </div>
    </Modal>
  </div>
</template>
