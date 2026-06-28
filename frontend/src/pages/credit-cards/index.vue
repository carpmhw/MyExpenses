<script setup lang="ts">
import { ref, computed, inject, watch, onMounted } from 'vue'
import { api } from '../../api'
import type { CreditCard } from '../../types'
import Card from '../../components/ui/Card.vue'
import Button from '../../components/ui/Button.vue'
import DataTable from '../../components/ui/DataTable.vue'
import Modal from '../../components/ui/Modal.vue'
import ConfirmDialog from '../../components/ui/ConfirmDialog.vue'
import Input from '../../components/ui/Input.vue'
import Select from '../../components/ui/Select.vue'
import Icon from '../../components/ui/Icon.vue'
import { formatMoney } from '../../utils/format'
import { CARD_NETWORK_OPTIONS, formatOptionalCreditCardText, normalizeOptionalCreditCardField } from '../../utils/creditCard'
import { usePagination } from '../../composables/usePagination'

const toast = inject<{ success: (m: string) => void; error: (m: string) => void }>('toast')!

const cards = ref<CreditCard[]>([])
const loading = ref(false)
const saving = ref(false)
const pagination = usePagination(1, 15)

const modalOpen = ref(false)
const editingItem = ref<CreditCard | null>(null)
const form = ref({ bankName: '', lastFourDigits: '', cardNetwork: '', statementDay: 1, dueDay: 1, creditLimit: 0, notes: '' })

const confirmOpen = ref(false)
const deletingId = ref<number | null>(null)

const columns = [
  { key: 'seq', label: '序號' },
  { key: 'createdAt', label: '建立日期' },
  { key: 'updatedAt', label: '修改日期' },
  { key: 'bankName', label: '發卡銀行' },
  { key: 'cardNetwork', label: '卡種' },
  { key: 'lastFourDigits', label: '卡號後四碼' },
  { key: 'creditLimit', label: '額度', align: 'right' as const },
  { key: 'statementDay', label: '結帳日' },
  { key: 'dueDay', label: '繳款截止日' },
  { key: 'notes', label: '備註' },
]

const formErrors = computed(() => {
  const errs: Record<string, string> = {}
  const lastFourDigits = form.value.lastFourDigits?.trim() ?? ''
  const notes = form.value.notes ?? ''
  if (!form.value.bankName?.trim()) errs.bankName = '請填寫發卡銀行'
  if (!lastFourDigits) errs.lastFourDigits = '請填寫卡號後四碼'
  else if (!/^\d{4}$/.test(lastFourDigits)) errs.lastFourDigits = '卡號後四碼必須為 4 位數字'
  if (notes.length > 200) errs.notes = '備註最多 200 字元'
  return errs
})

async function fetchList() {
  loading.value = true
  try {
    const result = await api.creditCards.list({ page: pagination.page.value, pageSize: pagination.pageSize.value })
    cards.value = result.items
    pagination.total.value = result.total
  } finally {
    loading.value = false
  }
}

function openCreate() {
  editingItem.value = null
  form.value = { bankName: '', lastFourDigits: '', cardNetwork: '', statementDay: 1, dueDay: 1, creditLimit: 0, notes: '' }
  modalOpen.value = true
}

function openEdit(item: CreditCard) {
  editingItem.value = item
  form.value = {
    bankName: item.bankName,
    lastFourDigits: item.lastFourDigits,
    cardNetwork: item.cardNetwork ?? '',
    statementDay: item.statementDay,
    dueDay: item.dueDay,
    creditLimit: item.creditLimit,
    notes: item.notes ?? '',
  }
  modalOpen.value = true
}

async function save() {
  const errs = formErrors.value
  if (Object.keys(errs).length > 0) return

  saving.value = true
  try {
    const payload = {
      bankName: form.value.bankName,
      lastFourDigits: form.value.lastFourDigits,
      cardNetwork: normalizeOptionalCreditCardField(form.value.cardNetwork),
      statementDay: form.value.statementDay,
      dueDay: form.value.dueDay,
      creditLimit: form.value.creditLimit,
      notes: normalizeOptionalCreditCardField(form.value.notes),
    }

    if (editingItem.value) {
      await api.creditCards.update(editingItem.value.id, payload)
      toast.success('信用卡已更新')
    } else {
      await api.creditCards.create(payload)
      toast.success('信用卡已建立')
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
    try {
      await api.creditCards.delete(deletingId.value)
      confirmOpen.value = false
      deletingId.value = null
      toast.success('信用卡已刪除')
      await fetchList()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : '刪除失敗')
    }
  }
}

function formatDate(dateStr: string | undefined | null) {
  if (!dateStr) return '-'
  return dateStr.slice(0, 10).replace(/-/g, '/')
}

function formatCreditLimit(amount: number) {
  return formatMoney(amount)
}

onMounted(fetchList)

watch(() => pagination.page.value, () => fetchList())
</script>

<template>
  <div class="p-4 lg:p-6">
    <div class="flex items-center justify-between mb-6">
      <div>
        <h1 class="text-2xl font-bold text-text-primary">信用卡管理</h1>
        <p class="text-xs text-text-secondary mt-1">所有信用卡一覽 · Credit Cards</p>
      </div>
      <Button @click="openCreate">+ 新增信用卡</Button>
    </div>

    <Card>
      <DataTable :columns="columns" :loading="loading" :items="cards">
        <template #empty>
          <div class="text-center text-text-tertiary py-4">尚無信用卡資料</div>
        </template>
        <tr v-for="(item, idx) in cards" :key="item.id" class="border-b border-border-default hover:bg-gray-100 dark:hover:bg-gray-700">
          <td class="py-3 px-4 text-text-secondary text-sm w-[60px]">{{ (pagination.page.value - 1) * pagination.pageSize.value + idx + 1 }}</td>
          <td class="py-3 px-4 text-text-primary text-sm whitespace-nowrap w-[110px]">{{ formatDate(item.createdAt) }}</td>
          <td class="py-3 px-4 text-text-primary text-sm whitespace-nowrap w-[110px]">{{ formatDate(item.updatedAt) }}</td>
          <td class="py-3 px-4 text-text-primary text-sm font-medium">{{ item.bankName }}</td>
          <td class="py-3 px-4 text-text-primary text-sm w-[140px]">{{ formatOptionalCreditCardText(item.cardNetwork) }}</td>
          <td class="py-3 px-4 text-text-primary text-sm w-[120px]">{{ item.lastFourDigits }}</td>
          <td class="py-3 px-4 text-text-primary font-bold text-sm w-[140px] text-right">{{ formatCreditLimit(item.creditLimit) }}</td>
          <td class="py-3 px-4 w-[120px]">
            <span
              class="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-blue-100 dark:bg-blue-900 text-blue-700 dark:text-blue-300"
            >
              每月{{ item.statementDay }}日
            </span>
          </td>
          <td class="py-3 px-4 w-[120px]">
            <span
              class="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-purple-100 dark:bg-purple-900 text-purple-700 dark:text-purple-300"
            >
              每月{{ item.dueDay }}日
            </span>
          </td>
          <td class="py-3 px-4 text-text-primary text-sm max-w-[220px]">
            <span class="block truncate" :title="item.notes ?? undefined">{{ formatOptionalCreditCardText(item.notes) }}</span>
          </td>
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

    <Modal :open="modalOpen" :title="editingItem ? '編輯信用卡' : '新增信用卡'" @update:open="modalOpen = $event">
      <form class="space-y-4" @submit.prevent="save">
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">發卡銀行</label>
          <Input v-model="form.bankName" :error="formErrors.bankName" />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">卡種</label>
          <Select v-model="form.cardNetwork" :options="CARD_NETWORK_OPTIONS" placeholder="選擇卡種（選填）" />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">卡號後四碼</label>
          <Input v-model="form.lastFourDigits" :maxlength="4" :error="formErrors.lastFourDigits" />
        </div>
        <div class="grid grid-cols-2 gap-4">
          <div>
            <label class="block text-sm font-medium text-text-primary mb-1">結帳日</label>
            <Input
              :model-value="String(form.statementDay)"
              type="number"
              :min="1"
              :max="31"
              @update:model-value="form.statementDay = Number($event) || 1"
            />
          </div>
          <div>
            <label class="block text-sm font-medium text-text-primary mb-1">繳款截止日</label>
            <Input
              :model-value="String(form.dueDay)"
              type="number"
              :min="1"
              :max="31"
              @update:model-value="form.dueDay = Number($event) || 1"
            />
          </div>
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">額度</label>
          <Input
            :model-value="String(form.creditLimit)"
            type="number"
            step="0.01"
            @update:model-value="form.creditLimit = Number($event) || 0"
          />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">備註</label>
          <Input v-model="form.notes" :maxlength="200" placeholder="例如：主力卡、網購用" :error="formErrors.notes" />
          <p class="mt-1 text-xs text-text-tertiary">{{ form.notes.length }} / 200</p>
        </div>
        <div class="flex justify-end gap-3 pt-2">
          <Button variant="ghost" type="button" @click="modalOpen = false">取消</Button>
          <Button type="submit" :loading="saving">儲存</Button>
        </div>
      </form>
    </Modal>

    <ConfirmDialog
      :open="confirmOpen"
      title="刪除信用卡"
      description="確定要刪除此信用卡資料嗎？相關的帳單記錄將受到影響。"
      variant="danger"
      @update:open="confirmOpen = $event"
      @confirm="doDelete"
    />
  </div>
</template>
