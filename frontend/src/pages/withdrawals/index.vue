<script setup lang="ts">
import { ref, computed, onMounted, watch, inject } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api } from '../../api'
import type { Withdrawal, BankAccount } from '../../types'
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

const toast = inject<{ success: (m: string) => void; error: (m: string) => void }>('toast')!

const route = useRoute()
const router = useRouter()
const pagination = usePagination(1, 15)

const withdrawals = ref<Withdrawal[]>([])
const bankAccounts = ref<BankAccount[]>([])
const loading = ref(false)
const saving = ref(false)

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
    const maxEnd = new Date(start)
    maxEnd.setDate(maxEnd.getDate() + 365)
    endDate.value = maxEnd.toISOString().slice(0, 10)
  }
}

const modalOpen = ref(false)
const editingItem = ref<Withdrawal | null>(null)
const form = ref({
  amount: 0,
  date: new Date().toISOString().slice(0, 10),
  bankAccountId: 0,
  description: '',
})

const confirmOpen = ref(false)
const deletingId = ref<number | null>(null)

const columns = [
  { key: 'seq', label: '序號' },
  { key: 'date', label: '日期' },
  { key: 'bankAccount', label: '銀行帳戶' },
  { key: 'amount', label: '金額', align: 'right' as const },
  { key: 'description', label: '說明' },
]

const bankAccountOptions = computed(() =>
  bankAccounts.value.map(b => ({ value: b.id, label: `${b.bankName} (${b.accountNumber})` }))
)

const stats = computed(() => {
  const list = withdrawals.value
  const total = list.reduce((sum, w) => sum + w.amount, 0)
  const count = pagination.total.value
  const max = list.length ? Math.max(...list.map(w => w.amount)) : 0
  const avg = count ? Math.round(total / count) : 0
  return { total, count, max, avg }
})

const formErrors = computed(() => {
  const errs: Record<string, string> = {}
  if (!form.value.amount || form.value.amount <= 0) errs.amount = '金額必須大於零'
  if (!form.value.bankAccountId || form.value.bankAccountId <= 0) errs.bankAccountId = '請選擇銀行帳戶'
  return errs
})

function getDefaultStartDate() {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-01`
}

function getDefaultEndDate() {
  const d = new Date()
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${new Date(d.getFullYear(), d.getMonth() + 1, 0).getDate()}`
}

function syncQueryString() {
  router.replace({
    query: {
      ...(startDate.value ? { startDate: startDate.value } : {}),
      ...(endDate.value ? { endDate: endDate.value } : {}),
      ...(pagination.page.value > 1 ? { page: String(pagination.page.value) } : {}),
    },
  })
}

function openCreate() {
  editingItem.value = null
  form.value = {
    amount: 0,
    date: new Date().toISOString().slice(0, 10),
    bankAccountId: bankAccounts.value[0]?.id || 0,
    description: '',
  }
  modalOpen.value = true
}

function openEdit(item: Withdrawal) {
  editingItem.value = item
  form.value = {
    amount: item.amount,
    date: item.date.slice(0, 10),
    bankAccountId: item.bankAccountId,
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
      await api.withdrawals.update(editingItem.value.id, form.value)
      toast.success('提款已更新')
    } else {
      await api.withdrawals.create(form.value)
      toast.success('提款已建立')
    }
    modalOpen.value = false
    await fetchWithdrawals()
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
    try {
      await api.withdrawals.delete(deletingId.value)
      confirmOpen.value = false
      deletingId.value = null
      toast.success('提款已刪除')
      await fetchWithdrawals()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : '刪除失敗')
    }
  }
}

async function fetchBankAccounts() {
  const result = await api.bankAccounts.list({ pageSize: 999 })
  bankAccounts.value = result.items
}

async function fetchWithdrawals() {
  loading.value = true
  try {
    const result = await api.withdrawals.list({
      page: pagination.page.value,
      pageSize: pagination.pageSize.value,
      startDate: startDate.value || undefined,
      endDate: endDate.value || undefined,
    })
    withdrawals.value = result.items
    pagination.total.value = result.total
  } finally {
    loading.value = false
  }
}

watch([startDate, endDate], () => {
  pagination.reset()
  syncQueryString()
  fetchWithdrawals()
})

watch(() => pagination.page.value, () => {
  syncQueryString()
  fetchWithdrawals()
})

onMounted(async () => {
  await fetchBankAccounts()
  await fetchWithdrawals()
})
</script>

<template>
  <div class="p-4 lg:p-6">
    <div class="flex items-center justify-between mb-6">
      <div>
        <h1 class="text-2xl font-bold text-text-primary">提款紀錄</h1>
        <p class="text-xs text-text-secondary mt-1">所有提款記錄 · Withdrawals</p>
      </div>
      <Button @click="openCreate">+ 新增提款</Button>
    </div>

    <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4 mb-6">
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl bg-red-500 dark:bg-red-700 flex items-center justify-center">
            <Icon name="banknote" :size="22" class="text-white" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">總提款金額</p>
            <p class="text-xl font-bold text-text-primary">{{ formatMoney(stats.total) }}</p>
          </div>
        </div>
      </Card>
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl bg-amber-500 dark:bg-amber-700 flex items-center justify-center">
            <Icon name="hash" :size="22" class="text-white" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">提款筆數</p>
            <p class="text-xl font-bold text-text-primary">{{ stats.count }} 筆</p>
          </div>
        </div>
      </Card>
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl bg-blue-500 dark:bg-blue-700 flex items-center justify-center">
            <Icon name="calculator" :size="22" class="text-white" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">平均每筆</p>
            <p class="text-xl font-bold text-text-primary">{{ formatMoney(stats.avg) }}</p>
          </div>
        </div>
      </Card>
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl bg-green-500 dark:bg-green-700 flex items-center justify-center">
            <Icon name="arrow-up" :size="22" class="text-white" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">最高單筆</p>
            <p class="text-xl font-bold text-text-primary">{{ formatMoney(stats.max) }}</p>
          </div>
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
      </div>

      <DataTable :columns="columns" :loading="loading" :items="withdrawals">
        <template #empty>
          <div class="text-center text-text-tertiary py-4">尚無提款紀錄</div>
        </template>
        <tr v-for="(item, idx) in withdrawals" :key="item.id" class="border-b border-border-default hover:bg-gray-100 dark:hover:bg-gray-700">
          <td class="py-3 px-4 text-text-secondary text-sm w-[60px]">{{ (pagination.page.value - 1) * pagination.pageSize.value + idx + 1 }}</td>
          <td class="py-3 px-4 text-text-primary text-sm whitespace-nowrap w-[100px]">{{ item.date.slice(0, 10) }}</td>
          <td class="py-3 px-4 w-[180px]">
            <span
              class="inline-flex items-center px-2 py-0.5 rounded-md text-xs border border-blue-300 dark:border-blue-700 bg-blue-50 dark:bg-blue-950 text-blue-700 dark:text-blue-300"
            >
              {{ item.bankAccount?.bankName }}
            </span>
          </td>
          <td class="py-3 px-4 text-right w-[130px]">
            <span class="font-semibold text-sm text-red-600 dark:text-red-400">
              {{ formatMoney(item.amount) }}
            </span>
          </td>
          <td class="py-3 px-4 text-text-tertiary text-sm">{{ item.description }}</td>
          <td class="py-3 px-4 w-[80px]">
            <div class="flex items-center gap-1">
              <button
                class="p-1.5 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 text-text-secondary cursor-pointer transition-colors"
                @click="openEdit(item)"
              >
                <Icon name="pencil" :size="16" />
              </button>
              <button
                class="p-1.5 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 text-red-500 cursor-pointer transition-colors"
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

    <Modal :open="modalOpen" :title="editingItem ? '編輯提款' : '新增提款'" @update:open="modalOpen = $event">
      <form class="space-y-4" @submit.prevent="save">
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
            class="w-full px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary"
            required
          />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">銀行帳戶</label>
          <Select
            :model-value="form.bankAccountId || ''"
            :options="bankAccountOptions"
            :error="formErrors.bankAccountId"
            @update:model-value="form.bankAccountId = Number($event)"
          />
          <p v-if="formErrors.bankAccountId" class="mt-1 text-xs text-red-500">{{ formErrors.bankAccountId }}</p>
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">說明</label>
          <Input v-model="form.description" placeholder="提款說明（選填）" />
        </div>
        <div class="flex justify-end gap-3 pt-2">
          <Button variant="ghost" type="button" @click="modalOpen = false">取消</Button>
          <Button type="submit" :loading="saving">儲存</Button>
        </div>
      </form>
    </Modal>

    <ConfirmDialog
      :open="confirmOpen"
      title="刪除提款"
      description="確定要刪除此提款記錄嗎？此操作無法復原。"
      variant="danger"
      @update:open="confirmOpen = $event"
      @confirm="doDelete"
    />
  </div>
</template>
