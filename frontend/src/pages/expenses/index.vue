<script setup lang="ts">
import { ref, computed, onMounted, watch, inject } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api } from '../../api'
import type { Category, Transaction, PaymentMethod, CreditCard, TransactionListSummary } from '../../types'
import Card from '../../components/ui/Card.vue'
import Button from '../../components/ui/Button.vue'
import DataTable from '../../components/ui/DataTable.vue'
import Modal from '../../components/ui/Modal.vue'
import ConfirmDialog from '../../components/ui/ConfirmDialog.vue'
import Input from '../../components/ui/Input.vue'
import Select from '../../components/ui/Select.vue'
import Icon from '../../components/ui/Icon.vue'
import { usePagination } from '../../composables/usePagination'
import { formatMoney } from '../../utils/format'
import { addCalendarDays, getCurrentMonthRange } from '../../utils/timezone'
import { useTimeZone } from '../../composables/useTimeZone'
import { createIdempotencyKeyState } from '../../utils/idempotency'

const toast = inject<{ success: (m: string) => void; error: (m: string) => void }>('toast')!
const timeZone = useTimeZone()


const route = useRoute()
const router = useRouter()
const pagination = usePagination(1, 15)

const transactions = ref<Transaction[]>([])
const summary = ref<TransactionListSummary>({
  totalAmount: 0,
  totalIncome: 0,
  totalExpense: 0,
  count: 0,
  dailyAverage: 0,
  maxAmount: 0,
})
const categories = ref<Category[]>([])
const paymentMethods = ref<PaymentMethod[]>([])
const loading = ref(false)
const saving = ref(false)

const activeTab = ref<'all' | 'Income' | 'Expense'>((route.query.type as 'all' | 'Income' | 'Expense') || 'all')
const search = ref((route.query.search as string) || '')
const selectedCategory = ref((route.query.categoryId as string) || '')
const startDate = ref((route.query.startDate as string) || getDefaultStartDate())
const endDate = ref((route.query.endDate as string) || getDefaultEndDate())


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
const form = ref({
  type: 'Expense' as 'Income' | 'Expense',
  amount: 0,
  date: timeZone.getToday(),
  categoryId: 0,
  description: '',
  notes: '',
  paymentMethodId: null as number | null,
})

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

const paymentMethodItems = computed(() => paymentMethods.value.map(p => ({ value: p.id, label: p.name })))



const installmentPeriods = ref(3)
const installmentCardId = ref<number | null>(null)
const creditCards = ref<CreditCard[]>([])
const installmentPurchaseIdempotency = createIdempotencyKeyState()

const creditCardOptions = computed(() =>
  creditCards.value.map(c => ({ value: c.id, label: `${c.bankName} (${c.lastFourDigits})` }))
)

const selectedPaymentMethod = computed(() =>
  form.value.paymentMethodId ? paymentMethods.value.find(p => p.id === form.value.paymentMethodId) : undefined
)

const isCreditCardSelected = computed(() =>
  selectedPaymentMethod.value?.systemCode === 'credit-card'
)

const stats = computed(() => {
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

const formErrors = computed(() => {
  const errs: Record<string, string> = {}
  if (!form.value.amount || form.value.amount <= 0) errs.amount = '金額必須大於零'
  if (!form.value.categoryId || form.value.categoryId <= 0) errs.categoryId = '請選擇類別'
  if (!form.value.description?.trim()) errs.description = '請填寫項目名稱'
  if (isCreditCardSelected.value && !editingItem.value && installmentPeriods.value <= 1) {
    errs.installmentPeriods = '期數必須大於 1'
  }
  if (isCreditCardSelected.value && !editingItem.value && !installmentCardId.value) {
    errs.installmentCardId = '請選擇信用卡'
  }
  return errs
})

const typeOptions = [
  { value: 'Expense', label: '支出' },
  { value: 'Income', label: '收入' },
]

const categoryOptions = computed(() =>
  categories.value
    .filter(c => c.type === form.value.type)
    .map(c => ({ value: c.id, label: c.name }))
)

function getDefaultStartDate() {
  return getCurrentMonthRange(new Date(), timeZone.timeZoneId.value).start
}

function getDefaultEndDate() {
  return getCurrentMonthRange(new Date(), timeZone.timeZoneId.value).end
}

async function fetchCategories() {
  const result = await api.categories.list({ pageSize: 999 })
  categories.value = result.items
}

async function fetchTransactions() {
  loading.value = true
  try {
    const result = await api.transactions.list({
      page: pagination.page.value,
      pageSize: pagination.pageSize.value,
      categoryId: selectedCategory.value ? Number(selectedCategory.value) : undefined,
      startDate: startDate.value || undefined,
      endDate: endDate.value || undefined,
      search: search.value || undefined,
      type: activeTab.value !== 'all' ? activeTab.value : undefined,
    })
    transactions.value = result.items
    summary.value = result.summary
    pagination.total.value = result.total
  } finally {
    loading.value = false
  }
}

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

watch([search, selectedCategory, startDate, endDate, activeTab], () => {
  pagination.reset()
  syncQueryString()
  fetchTransactions()
})

watch(() => pagination.page.value, () => {
  syncQueryString()
  fetchTransactions()
})

watch(() => form.value.paymentMethodId, (newVal, oldVal) => {
  if (oldVal !== null && newVal !== oldVal) {
    const wasCredit = paymentMethods.value.find(p => p.id === oldVal)?.systemCode === 'credit-card'
    const isCredit = paymentMethods.value.find(p => p.id === newVal)?.systemCode === 'credit-card'
    if (wasCredit && !isCredit) {
      installmentPeriods.value = 3
      installmentCardId.value = null
    }
  }
})

function openCreate() {
  editingItem.value = null
  form.value = {
    type: activeTab.value !== 'all' ? activeTab.value : 'Expense',
    amount: 0,
    date: timeZone.getToday(),
    categoryId: categories.value[0]?.id || 0,
    description: '',
    notes: '',
    paymentMethodId: null,
  }
  installmentPeriods.value = 3
  installmentCardId.value = null
  modalOpen.value = true
}

function openEdit(item: Transaction) {
  editingItem.value = item
  form.value = {
    type: item.type,
    amount: item.amount,
    date: item.date.slice(0, 10),
    categoryId: item.categoryId,
    description: item.description || '',
    notes: item.notes || '',
    paymentMethodId: item.paymentMethodId,
  }
  modalOpen.value = true
}

// Saves an ordinary transaction or one atomic installment purchase command.
async function save() {
  const errs = formErrors.value
  if (Object.keys(errs).length > 0) return

  saving.value = true
  let mutationSucceeded = false
  try {
    if (editingItem.value) {
      await api.transactions.update(editingItem.value.id, form.value)
      toast.success('交易已更新')
    } else {
      if (isCreditCardSelected.value && installmentCardId.value) {
        const purchaseRequest = {
          transaction: {
            type: 'Expense' as const,
            amount: form.value.amount,
            date: form.value.date,
            categoryId: form.value.categoryId,
            description: form.value.description,
            notes: form.value.notes,
            paymentMethodId: form.value.paymentMethodId ?? undefined,
          },
          installment: {
            cardId: installmentCardId.value,
            periods: installmentPeriods.value,
          },
        }
        const idempotencyKey = installmentPurchaseIdempotency.prepare(purchaseRequest)
        await api.installmentPurchases.create(purchaseRequest, idempotencyKey)
        installmentPurchaseIdempotency.clear()
        toast.success('交易與分期已建立')
      } else {
        await api.transactions.create(form.value)
        toast.success('交易已建立')
      }
    }
    modalOpen.value = false
    mutationSucceeded = true
  } catch (e) {
    toast.error(e instanceof Error ? e.message : '儲存失敗')
  } finally {
    saving.value = false
  }

  if (mutationSucceeded) {
    try {
      await fetchTransactions()
    } catch {
      toast.error('已儲存，但交易列表重新整理失敗')
    }
  }
}

function confirmDelete(id: number) {
  deletingId.value = id
  confirmOpen.value = true
}

async function doDelete() {
  if (deletingId.value !== null) {
    try {
      await api.transactions.delete(deletingId.value)
      confirmOpen.value = false
      deletingId.value = null
      toast.success('交易已刪除')
      await fetchTransactions()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : '刪除失敗')
    }
  }
}

function formatAmount(amount: number) {
  return formatMoney(amount)
}

async function fetchPaymentMethods() {
  const result = await api.paymentMethods.list({ pageSize: 999 })
  paymentMethods.value = result.items
}

onMounted(async () => {
  await fetchCategories()
  await fetchPaymentMethods()
  await fetchTransactions()
  const result = await api.creditCards.list({ pageSize: 999 })
  creditCards.value = result.items
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
              <p class="text-xl font-bold text-text-primary">{{ formatMoney(stats.total) }}</p>
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
              <p class="text-xl font-bold text-color-income-text">{{ formatMoney(stats.income) }}</p>
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
              <p class="text-xl font-bold text-color-expense-text">{{ formatMoney(stats.expense) }}</p>
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
              <p class="text-xl font-bold text-text-primary">{{ formatMoney(stats.total) }}</p>
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
              <p class="text-xl font-bold text-text-primary">{{ formatMoney(stats.dailyAvg) }}</p>
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
              <p class="text-xl font-bold text-text-primary">{{ formatMoney(stats.max) }}</p>
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

      <DataTable :columns="columns" :loading="loading" :items="transactions">
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

    <Modal :open="modalOpen" :title="editingItem ? '編輯交易' : (form.type === 'Income' ? '新增收入' : '新增支出')" @update:open="modalOpen = $event">
      <form class="space-y-4" @submit.prevent="save">
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">類型</label>
          <Select v-model="form.type" :options="typeOptions" />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">金額</label>
          <Input
            :model-value="form.amount || ''"
            type="number"
            step="0.01"
            :error="formErrors.amount"
            @update:model-value="form.amount = Number($event) || 0"
          />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">日期</label>
          <input
            v-model="form.date"
            type="date"
            class="w-full px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
            required
          />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">類別</label>
          <Select
            :model-value="form.categoryId || ''"
            :options="categoryOptions"
            :error="formErrors.categoryId"
            @update:model-value="form.categoryId = Number($event)"
          />
          <p v-if="formErrors.categoryId" class="mt-1 text-xs text-color-expense-text">{{ formErrors.categoryId }}</p>
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">項目</label>
          <Input
            v-model="form.description"
            placeholder="e.g. 早餐店鐵板麵"
            :error="formErrors.description"
          />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">支付方式</label>
          <Select
            :model-value="form.paymentMethodId ?? ''"
            :options="paymentMethodItems"
            placeholder="選擇支付方式"
            @update:model-value="form.paymentMethodId = $event ? Number($event) : null"
          />
        </div>
        <template v-if="form.type === 'Expense' && !editingItem && isCreditCardSelected">
          <div class="grid grid-cols-2 gap-4">
            <div>
              <label class="block text-sm font-medium text-text-primary mb-1">分期期數</label>
              <Input
                :model-value="installmentPeriods || ''"
                type="number"
                :min="2"
                @update:model-value="installmentPeriods = Number($event) || 3"
              />
              <p v-if="formErrors.installmentPeriods" class="mt-1 text-xs text-color-expense-text">{{ formErrors.installmentPeriods }}</p>
            </div>
            <div>
              <label class="block text-sm font-medium text-text-primary mb-1">信用卡</label>
              <Select
                :model-value="installmentCardId ?? ''"
                :options="creditCardOptions"
                placeholder="選擇信用卡"
                @update:model-value="installmentCardId = Number($event) || null"
              />
              <p v-if="formErrors.installmentCardId" class="mt-1 text-xs text-color-expense-text">{{ formErrors.installmentCardId }}</p>
            </div>
          </div>
        </template>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">備註</label>
          <Input v-model="form.notes" placeholder="備註說明" />
        </div>
        <div class="flex justify-end gap-3 pt-2">
          <Button variant="ghost" type="button" @click="modalOpen = false">取消</Button>
          <Button type="submit" :loading="saving">儲存</Button>
        </div>
      </form>
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
