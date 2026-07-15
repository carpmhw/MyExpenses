<script setup lang="ts">
import { ref, computed, inject, watch, onMounted } from 'vue'
import { api } from '../../api'
import type { Installment, CreditCard, CreditCardBill } from '../../types'
import Card from '../../components/ui/Card.vue'
import Button from '../../components/ui/Button.vue'
import DataTable from '../../components/ui/DataTable.vue'
import Modal from '../../components/ui/Modal.vue'
import ConfirmDialog from '../../components/ui/ConfirmDialog.vue'
import Input from '../../components/ui/Input.vue'
import Select from '../../components/ui/Select.vue'
import Icon from '../../components/ui/Icon.vue'
import { formatMoney } from '../../utils/format'
import { usePagination } from '../../composables/usePagination'
import { useTimeZone } from '../../composables/useTimeZone'
import { addCalendarDays, formatDateOnly, getCurrentMonthRange, isDateOnlyBefore } from '../../utils/timezone'

const toast = inject<{ success: (m: string) => void; error: (m: string) => void }>('toast')!
const timeZone = useTimeZone()

const installments = ref<Installment[]>([])
const creditCards = ref<CreditCard[]>([])
const unpaidBills = ref<CreditCardBill[]>([])
const loading = ref(false)
const saving = ref(false)
const pagination = usePagination(1, 15)

const filterCardId = ref<number | ''>('')
const filterStatus = ref<string>('')

function getDefaultStartDate(): string {
  return getCurrentMonthRange(new Date(), timeZone.timeZoneId.value).start
}
function getDefaultEndDate(): string {
  return getCurrentMonthRange(new Date(), timeZone.timeZoneId.value).end
}

const startDate = ref(getDefaultStartDate())
const endDate = ref(getDefaultEndDate())

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
const editingItem = ref<Installment | null>(null)
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
const scheduleInstallment = ref<Installment | null>(null)

const confirmOpen = ref(false)
const deletingId = ref<number | null>(null)

const paymentConfirmOpen = ref(false)
const payingPaymentId = ref<number | null>(null)
const markingAsPaid = ref(true)
const paidDate = ref(timeZone.getToday())

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
  const total = installments.value.length
  const active = installments.value.filter(i => i.status === 'Active').length
  const currentMonth = getCurrentMonthRange(new Date(), timeZone.timeZoneId.value)
  let monthlyDue = 0
  for (const inst of installments.value) {
    if (inst.status !== 'Active') continue
    for (const p of inst.payments || []) {
      if (p.isPaid || !p.dueDate) continue
      if (p.dueDate >= currentMonth.start && p.dueDate <= currentMonth.end) {
        monthlyDue += p.amount
      }
    }
  }
  return { total, active, monthlyDue }
})

const hasPaidPayments = computed(() =>
  editingItem.value?.payments?.some(p => p.isPaid) ?? false
)

const formErrors = computed(() => {
  const errs: Record<string, string> = {}
  if (!form.value.totalAmount || form.value.totalAmount <= 0) errs.totalAmount = '總金額必須大於零'
  if (!form.value.periods || form.value.periods < 1) errs.periods = '期數必須大於 0'
  if (!form.value.cardId) errs.cardId = '請選擇信用卡'
  if (!form.value.purchaseDate) errs.purchaseDate = '請選擇刷卡日期'
  if (!form.value.description?.trim()) errs.description = '請填寫交易描述'
  return errs
})

watch([() => form.value.totalAmount, () => form.value.periods], () => {
  if (form.value.totalAmount > 0 && form.value.periods > 0) {
    form.value.perPeriod = Math.floor(form.value.totalAmount / form.value.periods)
  } else {
    form.value.perPeriod = 0
  }
})

async function fetchList() {
  loading.value = true
  try {
    const result = await api.installments.list({
      page: pagination.page.value,
      pageSize: pagination.pageSize.value,
      cardId: filterCardId.value || undefined,
      dateStart: startDate.value || undefined,
      dateEnd: endDate.value || undefined,
      status: filterStatus.value || undefined,
    })
    installments.value = result.items
    pagination.total.value = result.total
  } finally {
    loading.value = false
  }
  await fetchUnpaidBills()
}

async function fetchUnpaidBills() {
  try {
    const result = await api.creditCardBills.list({
      isPaid: false,
      cardId: filterCardId.value || undefined,
    })
    unpaidBills.value = result
  } catch {
    unpaidBills.value = []
  }
}

async function fetchCreditCards() {
  const result = await api.creditCards.list({ pageSize: 999 })
  creditCards.value = result.items
}

function openCreate() {
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

function openEdit(item: Installment) {
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

async function save() {
  const errs = formErrors.value
  if (Object.keys(errs).length > 0) return

  saving.value = true
  try {
    if (editingItem.value) {
      await api.installments.update(editingItem.value.id, form.value)
      toast.success('分期已更新')
    } else {
      await api.installments.create(form.value)
      toast.success('分期已建立')
    }
    modalOpen.value = false
    await fetchList()
  } catch (e) {
    toast.error(e instanceof Error ? e.message : '儲存失敗')
  } finally {
    saving.value = false
  }
}

function confirmDelete(id: number) {
  deletingId.value = id
  confirmOpen.value = true
}

async function doDelete() {
  if (deletingId.value !== null) {
    const id = deletingId.value
    confirmOpen.value = false
    deletingId.value = null

    try {
      await api.installments.delete(id)
      toast.success('分期已刪除')
      await fetchList()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : '刪除失敗')
    }
  }
}

function openSchedule(item: Installment) {
  scheduleInstallment.value = item
  scheduleOpen.value = true
}

function confirmMarkPayment(paymentId: number, isPaid: boolean) {
  payingPaymentId.value = paymentId
  markingAsPaid.value = !isPaid
  paidDate.value = timeZone.getToday()
  paymentConfirmOpen.value = true
}

async function doMarkPayment() {
  if (!scheduleInstallment.value || payingPaymentId.value === null) return

  const id = scheduleInstallment.value.id
  const paymentId = payingPaymentId.value

  if (markingAsPaid.value && !paidDate.value) {
    toast.error('請選擇實際繳款日')
    return
  }

  saving.value = true

  try {
    await api.installments.markPayment(id, paymentId, markingAsPaid.value ? paidDate.value : undefined)
    paymentConfirmOpen.value = false
    payingPaymentId.value = null
    toast.success(markingAsPaid.value ? '已標記為已繳款' : '已取消繳款標記')
    await fetchList()
    const updated = await api.installments.get(id)
    scheduleInstallment.value = updated
  } catch (e) {
    toast.error(e instanceof Error ? e.message : '標記失敗')
  } finally {
    saving.value = false
  }
}

function getCardDisplay(inst: Installment): string {
  if (inst.card) return `${inst.card.bankName} (${inst.card.lastFourDigits})`
  return '-'
}

function formatDate(dateStr: string | undefined | null) {
  if (!dateStr) return '-'
  return formatDateOnly(dateStr)
}

function progressPercent(inst: Installment): number {
  if (inst.periods === 0) return 0
  return Math.round(((inst.periods - inst.remainingPeriods) / inst.periods) * 100)
}

onMounted(async () => {
  await fetchCreditCards()
  await fetchList()
})

watch(() => pagination.page.value, () => fetchList())
watch([filterCardId, filterStatus, startDate, endDate], () => {
  pagination.reset()
  fetchList()
})
</script>

<template>
  <div class="p-4 lg:p-6">
    <div class="flex items-center justify-between mb-6">
      <div>
        <h1 class="text-2xl font-bold text-text-primary">信用卡分期</h1>
        <p class="text-xs text-text-secondary mt-1">分期付款管理 · Installments</p>
      </div>
      <Button @click="openCreate">+ 新增分期</Button>
    </div>

    <div class="grid grid-cols-1 sm:grid-cols-3 gap-4 mb-6">
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl bg-blue-500 dark:bg-blue-700 flex items-center justify-center">
            <Icon name="receipt" :size="22" class="text-white" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">總分期筆數</p>
            <p class="text-xl font-bold text-text-primary">{{ stats.total }} 筆</p>
          </div>
        </div>
      </Card>
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl bg-amber-500 dark:bg-amber-700 flex items-center justify-center">
            <Icon name="clock" :size="22" class="text-white" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">進行中</p>
            <p class="text-xl font-bold text-text-primary">{{ stats.active }} 筆</p>
          </div>
        </div>
      </Card>
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl bg-green-500 dark:bg-green-700 flex items-center justify-center">
            <Icon name="credit-card" :size="22" class="text-white" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">本月應繳總額</p>
            <p class="text-xl font-bold text-green-600">{{ formatMoney(stats.monthlyDue) }}</p>
          </div>
        </div>
      </Card>
    </div>

    <div v-if="unpaidBills.length > 0" class="mb-6">
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
              <td class="py-2 pr-2" :class="isDateOnlyBefore(bill.dueDate, timeZone.getToday()) ? 'text-red-500 font-medium' : 'text-text-primary'">
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
        <div class="flex items-center gap-2 text-sm text-green-600">
          <Icon name="check-circle" :size="18" />
          <span>目前無未繳帳單</span>
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
          class="px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary"
        />
        <span class="text-text-secondary">~</span>
        <input
          v-model="endDate"
          type="date"
          @change="validateDateRange"
          class="px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary"
        />
        <span class="text-xs text-text-tertiary">（最多 1 年）</span>
        <span class="text-sm font-medium text-text-primary ml-2">信用卡</span>
        <select
          v-model="filterCardId"
          class="px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary"
        >
          <option value="">全部</option>
          <option v-for="c in creditCards" :key="c.id" :value="c.id">{{ c.bankName }} ({{ c.lastFourDigits }})</option>
        </select>
        <span class="text-sm font-medium text-text-primary ml-2">狀態</span>
        <select
          v-model="filterStatus"
          class="px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary"
        >
          <option value="">全部</option>
          <option value="Active">進行中</option>
          <option value="PaidOff">已結清</option>
        </select>
      </div>
      <DataTable :columns="columns" :loading="loading" :items="installments">
        <template #empty>
          <div class="text-center text-text-tertiary py-4">尚無分期資料</div>
        </template>
        <tr v-for="(item, idx) in installments" :key="item.id" class="border-b border-border-default hover:bg-gray-100 dark:hover:bg-gray-700">
          <td class="py-3 px-4 text-text-secondary text-sm w-[60px]">{{ (pagination.page.value - 1) * pagination.pageSize.value + idx + 1 }}</td>
          <td class="py-3 px-4 text-text-primary text-sm whitespace-nowrap w-[100px]">{{ formatDate(item.purchaseDate) }}</td>
          <td class="py-3 px-4 text-text-primary text-sm">{{ item.description }}</td>
          <td class="py-3 px-4 text-text-primary text-sm">{{ getCardDisplay(item) }}</td>
          <td class="py-3 px-4 text-text-primary font-bold text-sm w-[130px] text-right">{{ formatMoney(item.totalAmount) }}</td>
          <td class="py-3 px-4 text-text-primary text-sm w-[60px]">{{ item.periods }} 期</td>
          <td class="py-3 px-4 text-text-primary text-sm w-[120px] text-right">{{ formatMoney(item.perPeriod) }}</td>
          <td class="py-3 px-4 text-text-primary text-sm w-[90px]">{{ item.remainingPeriods }}</td>
          <td class="py-3 px-4 w-[90px]">
            <span
              class="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium"
              :class="item.status === 'PaidOff'
                ? 'bg-green-100 dark:bg-green-900 text-green-700 dark:text-green-300'
                : 'bg-blue-100 dark:bg-blue-900 text-blue-700 dark:text-blue-300'"
            >
              {{ item.status === 'PaidOff' ? '已結清' : '進行中' }}
            </span>
          </td>
          <td class="py-3 px-4 w-[130px]">
            <div class="flex items-center gap-2">
              <div class="flex-1 h-2 bg-gray-200 dark:bg-gray-600 rounded-full overflow-hidden">
                <div
                  class="h-full rounded-full transition-all"
                  :class="progressPercent(item) >= 100 ? 'bg-green-500' : 'bg-blue-500'"
                  :style="{ width: `${progressPercent(item)}%` }"
                />
              </div>
              <span class="text-xs text-text-secondary w-[40px]">{{ progressPercent(item) }}%</span>
            </div>
          </td>
          <td class="py-3 px-4 w-[120px]">
            <div class="flex items-center gap-1">
              <button
                class="p-1.5 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 text-text-secondary cursor-pointer transition-colors"
                title="檢視時程"
                @click="openSchedule(item)"
              >
                <Icon name="calendar" :size="16" />
              </button>
              <button
                class="p-1.5 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 text-text-secondary cursor-pointer transition-colors"
                title="編輯"
                @click="openEdit(item)"
              >
                <Icon name="pencil" :size="16" />
              </button>
              <button
                class="p-1.5 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 text-red-500 cursor-pointer transition-colors"
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

    <Modal :open="modalOpen" :title="editingItem ? '編輯分期' : '新增分期'" @update:open="modalOpen = $event">
      <form class="space-y-4" @submit.prevent="save">
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">交易描述</label>
          <Input v-model="form.description" :error="formErrors.description" />
        </div>
        <div class="grid grid-cols-2 gap-4">
          <div>
            <label class="block text-sm font-medium text-text-primary mb-1">總金額</label>
            <Input
              :model-value="form.totalAmount || ''"
              type="number"
              step="0.01"
              :disabled="hasPaidPayments"
              :error="formErrors.totalAmount"
              @update:model-value="form.totalAmount = Number($event) || 0"
            />
            <p v-if="hasPaidPayments" class="mt-1 text-xs text-amber-500">已有繳款記錄，不可修改</p>
          </div>
          <div>
            <label class="block text-sm font-medium text-text-primary mb-1">期數</label>
            <Input
              :model-value="form.periods || ''"
              type="number"
              :min="1"
              :disabled="hasPaidPayments"
              :error="formErrors.periods"
              @update:model-value="form.periods = Number($event) || 1"
            />
            <p v-if="hasPaidPayments" class="mt-1 text-xs text-amber-500">已有繳款記錄，不可修改</p>
          </div>
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">信用卡</label>
          <Select
            :model-value="form.cardId ?? ''"
            :options="cardOptions"
            placeholder="選擇信用卡"
            :disabled="hasPaidPayments"
            @update:model-value="form.cardId = Number($event) || null"
          />
          <p v-if="hasPaidPayments" class="mt-1 text-xs text-amber-500">已有繳款記錄，不可修改</p>
          <p v-else-if="formErrors.cardId" class="mt-1 text-xs text-red-500">{{ formErrors.cardId }}</p>
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">刷卡日期</label>
          <input
            v-model="form.purchaseDate"
            type="date"
            :disabled="hasPaidPayments"
            class="w-full px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary disabled:opacity-60 disabled:cursor-not-allowed"
          />
          <p v-if="hasPaidPayments" class="mt-1 text-xs text-amber-500">已有繳款記錄，不可修改</p>
          <p v-else-if="formErrors.purchaseDate" class="mt-1 text-xs text-red-500">{{ formErrors.purchaseDate }}</p>
        </div>
        <div class="flex justify-end gap-3 pt-2">
          <Button variant="ghost" type="button" @click="modalOpen = false">取消</Button>
          <Button type="submit" :loading="saving">儲存</Button>
        </div>
      </form>
    </Modal>

    <Modal :open="scheduleOpen" title="付款時程" size="lg" @update:open="scheduleOpen = $event">
      <div v-if="scheduleInstallment" class="space-y-4">
        <div class="flex items-center justify-between text-sm">
          <span class="text-text-secondary">
            {{ scheduleInstallment.description }} · {{ scheduleInstallment.periods }} 期
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
                    ? 'bg-green-100 dark:bg-green-900 text-green-700 dark:text-green-300'
                    : 'bg-gray-100 dark:bg-gray-700 text-gray-600 dark:text-gray-400'"
                >
                  {{ p.isPaid ? '已繳' : '未繳' }}
                </span>
              </td>
              <td class="py-2 px-2 text-center">
                <button
                  v-if="!p.isPaid"
                  class="px-2 py-1 rounded text-xs font-medium bg-green-50 dark:bg-green-950 text-green-700 dark:text-green-300 hover:bg-green-100 dark:hover:bg-green-900 cursor-pointer transition-colors"
                  @click="confirmMarkPayment(p.id, false)"
                >
                  標記已繳
                </button>
                <button
                  v-else
                  class="px-2 py-1 rounded text-xs font-medium bg-gray-50 dark:bg-gray-800 text-text-secondary hover:bg-gray-100 dark:hover:bg-gray-700 cursor-pointer transition-colors"
                  @click="confirmMarkPayment(p.id, true)"
                >
                  取消已繳
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </Modal>

    <ConfirmDialog
      :open="confirmOpen"
      title="刪除分期"
      description="確定要刪除此分期記錄嗎？相關的付款記錄也將一併刪除。"
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
          class="w-full px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary"
        />
      </div>
      <div class="flex justify-end gap-3">
        <Button variant="ghost" type="button" @click="paymentConfirmOpen = false">取消</Button>
        <Button type="button" :loading="saving" @click="doMarkPayment">確認</Button>
      </div>
    </Modal>
  </div>
</template>
