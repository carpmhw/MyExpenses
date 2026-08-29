<script setup lang="ts">
import { ref, computed, inject, watch, onMounted } from 'vue'
import { api } from '../../api'
import type { BankAccountListItem, CurrencyCode } from '../../types'
import Card from '../../components/ui/Card.vue'
import Button from '../../components/ui/Button.vue'
import DataTable from '../../components/ui/DataTable.vue'
import Modal from '../../components/ui/Modal.vue'
import ConfirmDialog from '../../components/ui/ConfirmDialog.vue'
import Input from '../../components/ui/Input.vue'
import Select from '../../components/ui/Select.vue'
import Icon from '../../components/ui/Icon.vue'
import { formatCurrency } from '../../utils/format'
import { CURRENCY_OPTIONS } from '../../utils/currency'
import { createBankAccountForm, hasCurrencyChanged, type BankAccountForm } from '../../utils/bankAccount'
import { createLatestRequestGuard } from '../../utils/latestRequest'
import { usePagination } from '../../composables/usePagination'
import { useTimeZone } from '../../composables/useTimeZone'

const toast = inject<{ success: (m: string) => void; error: (m: string) => void }>('toast')!
const timeZone = useTimeZone()

const accounts = ref<BankAccountListItem[]>([])
const totalBalanceInBaseCurrency = ref<number | null>(null)
const baseCurrency = ref<CurrencyCode>('TWD')
const exchangeRateUpdatedAt = ref<string | null>(null)
const exchangeRateIsStale = ref(false)
const conversionAvailable = ref(true)
const bankNameFilter = ref('')
const loading = ref(false)
const saving = ref(false)
const listError = ref<string | null>(null)
const pagination = usePagination(1, 15)

const modalOpen = ref(false)
const editingItem = ref<BankAccountListItem | null>(null)
const form = ref<BankAccountForm>(createBankAccountForm())

const confirmOpen = ref(false)
const deletingId = ref<number | null>(null)
const snapshotLoading = ref(false)
const listRequestGuard = createLatestRequestGuard()

const columns = [
  { key: 'seq', label: '序號' },
  { key: 'createdAt', label: '建立日期' },
  { key: 'updatedAt', label: '修改日期' },
  { key: 'bankName', label: '銀行名稱' },
  { key: 'lastFiveDigits', label: '帳號後五碼' },
  { key: 'currencyCode', label: '幣別', align: 'center' as const },
  { key: 'balance', label: '原幣餘額', align: 'right' as const },
  { key: 'convertedBalance', label: '折合 TWD', align: 'right' as const },
  { key: 'accountType', label: '帳戶類型' },
]

// 以系統時區格式化帳戶建立與修改日期。
function formatDate(dateStr: string) {
  return timeZone.formatDateTime(dateStr).slice(0, 10)
}

// 以系統時區格式化匯率更新時間。
function formatRateUpdatedAt(dateStr: string | null): string {
  return dateStr ? timeZone.formatDateTime(dateStr) : '無外部匯率'
}

const formErrors = computed(() => {
  const errs: Record<string, string> = {}
  const accountNumber = form.value.accountNumber?.trim() ?? ''
  if (!form.value.bankName?.trim()) errs.bankName = '請填寫銀行名稱'
  if (!accountNumber) errs.accountNumber = '請填寫帳號後五碼'
  else if (!/^\d{5}$/.test(accountNumber)) errs.accountNumber = '帳號後五碼必須為 5 位數字'
  return errs
})

const currencyChanged = computed(() =>
  hasCurrencyChanged(editingItem.value?.currencyCode, form.value.currencyCode),
)

// 取得目前頁面與銀行名稱篩選條件的帳戶資料。
async function fetchList() {
  const requestId = listRequestGuard.next()
  loading.value = true
  listError.value = null
  try {
    const result = await api.bankAccounts.list({ page: pagination.page.value, pageSize: pagination.pageSize.value, bankName: bankNameFilter.value })
    if (!listRequestGuard.isLatest(requestId)) return
    accounts.value = result.items
    pagination.total.value = result.total
    totalBalanceInBaseCurrency.value = result.totalBalanceInBaseCurrency
    baseCurrency.value = result.baseCurrency
    exchangeRateUpdatedAt.value = result.exchangeRateUpdatedAt
    exchangeRateIsStale.value = result.exchangeRateIsStale
    conversionAvailable.value = result.conversionAvailable
  } catch (e) {
    if (listRequestGuard.isLatest(requestId)) {
      listError.value = e instanceof Error ? e.message : '載入銀行帳戶失敗，請重試'
    }
  } finally {
    if (listRequestGuard.isLatest(requestId)) loading.value = false
  }
}

// 開啟新增帳戶表單並重設為 TWD 預設幣別。
function openCreate() {
  editingItem.value = null
  form.value = createBankAccountForm()
  modalOpen.value = true
}

// 開啟編輯表單並回填帳戶原幣資料。
function openEdit(item: BankAccountListItem) {
  editingItem.value = item
  form.value = createBankAccountForm(item)
  modalOpen.value = true
}

// 驗證並提交新增或編輯帳戶資料。
async function save() {
  const errs = formErrors.value
  if (Object.keys(errs).length > 0) return

  saving.value = true
  try {
    if (editingItem.value) {
      await api.bankAccounts.update(editingItem.value.id, form.value)
      toast.success('帳戶已更新')
    } else {
      await api.bankAccounts.create(form.value)
      toast.success('帳戶已建立')
    }
    modalOpen.value = false
    await fetchList()
  } catch (e) {
    toast.error(e instanceof Error ? e.message : '儲存失敗')
  } finally {
    saving.value = false
  }
}

// 開啟指定帳戶的刪除確認對話框。
function confirmDelete(id: number) {
  deletingId.value = id
  confirmOpen.value = true
}

// 刪除已確認的銀行帳戶並重新整理列表。
async function doDelete() {
  if (deletingId.value !== null) {
    try {
      await api.bankAccounts.delete(deletingId.value)
      confirmOpen.value = false
      deletingId.value = null
      toast.success('帳戶已刪除')
      await fetchList()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : '刪除失敗')
    }
  }
}

// 建立包含目前資產資料的財務快照。
async function takeSnapshot() {
  snapshotLoading.value = true
  try {
    const result = await api.snapshots.create()
    toast.success(`快照已建立: ${result.name}`)
  } catch (e) {
    toast.error(e instanceof Error ? e.message : '建立快照失敗')
  } finally {
    snapshotLoading.value = false
  }
}

onMounted(fetchList)

watch(() => pagination.page.value, () => fetchList())
// Resets filtered searches to the first page so counts and totals stay aligned.
watch(bankNameFilter, () => {
  if (pagination.page.value !== 1) {
    pagination.page.value = 1
    return
  }
  fetchList()
})
</script>

<template>
  <div class="p-4 lg:p-6">
    <div class="flex items-center justify-between mb-6">
      <div>
        <h1 class="text-2xl font-bold text-text-primary">銀行帳戶管理</h1>
        <p class="text-xs text-text-secondary mt-1">所有帳戶一覽 · Bank Accounts</p>
      </div>
      <div class="flex items-center gap-2">
        <Button :loading="snapshotLoading" @click="takeSnapshot" title="紀錄所有銀行帳戶與股票的當前狀態">📷 拍照</Button>
        <Button @click="openCreate">+ 新增帳戶</Button>
      </div>
    </div>

    <div class="grid grid-cols-1 sm:grid-cols-3 gap-4 mb-6">
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl bg-color-income flex items-center justify-center">
            <Icon name="wallet" :size="22" class="text-text-on-accent" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">總計金額</p>
            <p class="text-xl font-bold text-text-primary">
              {{ totalBalanceInBaseCurrency === null ? '不可用' : formatCurrency(totalBalanceInBaseCurrency, baseCurrency) }}
            </p>
            <p class="text-xs text-text-tertiary mt-1">符合目前篩選條件 · 基準 {{ baseCurrency }}</p>
            <p v-if="!conversionAvailable" class="text-xs text-color-warning-text mt-1">匯率不可用，保留原幣資料</p>
            <p v-else-if="exchangeRateIsStale" class="text-xs text-color-warning-text mt-1">
              使用過期匯率 · {{ formatRateUpdatedAt(exchangeRateUpdatedAt) }}
            </p>
            <p v-else-if="exchangeRateUpdatedAt" class="text-xs text-text-tertiary mt-1">
              匯率更新於 {{ formatRateUpdatedAt(exchangeRateUpdatedAt) }}
            </p>
          </div>
        </div>
      </Card>
    </div>

    <Card>
      <div class="flex flex-wrap items-center gap-3 mb-4">
        <span class="text-sm font-medium text-text-primary">銀行名稱</span>
        <input
          v-model="bankNameFilter"
          type="text"
          placeholder="輸入銀行名稱關鍵字"
          class="w-full sm:w-64 px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary placeholder:text-text-tertiary"
        />
      </div>
      <DataTable :columns="columns" :loading="loading" :items="accounts" :error="listError" :retry="fetchList">
        <template #empty>
          <div class="text-center text-text-tertiary py-4">尚無銀行帳戶資料</div>
        </template>
        <tr v-for="(item, index) in accounts" :key="item.id" class="border-b border-border-default hover:bg-bg-raised">
          <td class="py-3 px-4 text-text-secondary text-sm w-[60px]">{{ (pagination.page.value - 1) * pagination.pageSize.value + index + 1 }}</td>
          <td class="py-3 px-4 text-text-secondary w-[110px]">{{ formatDate(item.createdAt) }}</td>
          <td class="py-3 px-4 text-text-secondary w-[110px]">{{ formatDate(item.updatedAt) }}</td>
          <td class="py-3 px-4 text-text-primary font-medium whitespace-nowrap">{{ item.bankName }}</td>
          <td class="py-3 px-4 text-text-secondary font-mono w-[130px] whitespace-nowrap">{{ item.accountNumber.slice(-5) }}</td>
          <td class="py-3 px-4 text-text-primary font-medium text-center whitespace-nowrap">{{ item.currencyCode }}</td>
          <td class="py-3 px-4 text-text-primary font-bold text-sm w-[150px] text-right whitespace-nowrap">{{ formatCurrency(item.balance, item.currencyCode) }}</td>
          <td class="py-3 px-4 text-text-secondary text-sm w-[150px] text-right whitespace-nowrap">
            {{ formatCurrency(item.convertedBalance, baseCurrency) }}
          </td>
          <td class="py-3 px-4 text-text-secondary w-[250px] whitespace-nowrap">{{ item.accountType }}</td>
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

    <Modal :open="modalOpen" :title="editingItem ? '編輯帳戶' : '新增帳戶'" @update:open="modalOpen = $event">
      <form class="space-y-4" @submit.prevent="save">
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">銀行名稱</label>
          <Input v-model="form.bankName" :error="formErrors.bankName" />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">帳號後五碼</label>
          <Input v-model="form.accountNumber" :maxlength="5" :error="formErrors.accountNumber" />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">餘額</label>
          <Input
            :model-value="String(form.balance)"
            type="number"
            step="0.01"
            @update:model-value="form.balance = Number($event) || 0"
          />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">類型</label>
          <Input v-model="form.accountType" placeholder="e.g. 活存、定存" />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1" for="bank-account-currency">幣別</label>
          <Select id="bank-account-currency" v-model="form.currencyCode" :options="CURRENCY_OPTIONS" />
          <p v-if="currencyChanged" class="mt-1 text-xs text-color-warning-text">
            變更幣別不會自動換算餘額，請確認輸入值已是新的原幣金額。
          </p>
        </div>
        <div class="flex justify-end gap-3 pt-2">
          <Button variant="ghost" type="button" @click="modalOpen = false">取消</Button>
          <Button type="submit" :loading="saving">儲存</Button>
        </div>
      </form>
    </Modal>

    <ConfirmDialog
      :open="confirmOpen"
      title="刪除帳戶"
      description="確定要刪除此銀行帳戶嗎？相關的提款記錄將受到影響。"
      variant="danger"
      @update:open="confirmOpen = $event"
      @confirm="doDelete"
    />
  </div>
</template>
