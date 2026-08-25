<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type { StockLedgerTransactionRequest, StockListItem, StockTransactionListItem } from '../../types'
import Modal from '../ui/Modal.vue'
import Button from '../ui/Button.vue'
import Input from '../ui/Input.vue'
import { useTimeZone } from '../../composables/useTimeZone'

type EditableTransactionType = 'Buy' | 'Sell' | 'Dividend'

interface TransactionFormState {
  stockId: number | null
  type: EditableTransactionType
  tradeDate: string
  shares: string
  price: string
  fee: string
  tax: string
  cashAmount: string
  notes: string
}

const props = defineProps<{
  open: boolean
  stocks: StockListItem[]
  stockId: number | null
  transaction: StockTransactionListItem | null
  loading: boolean
  errorMessage?: string
}>()

const emit = defineEmits<{
  'update:open': [value: boolean]
  save: [request: StockLedgerTransactionRequest]
}>()

const timeZone = useTimeZone()

// 建立空白交易表單，所有數值先保留字串以避免原生 input 的空值被轉成零。
function createEmptyForm(stockId: number | null): TransactionFormState {
  return {
    stockId,
    type: 'Buy',
    tradeDate: timeZone.getToday(),
    shares: '',
    price: '',
    fee: '0',
    tax: '0',
    cashAmount: '',
    notes: '',
  }
}

const form = ref<TransactionFormState>(createEmptyForm(props.stockId ?? props.stocks[0]?.id ?? null))

const errors = computed(() => {
  const result: Record<string, string> = {}
  if (!form.value.stockId) result.stockId = '請選擇股票'
  if (!form.value.tradeDate) result.tradeDate = '請選擇交易日期'
  if (form.value.type === 'Dividend') {
    if (!(Number(form.value.cashAmount) > 0)) result.cashAmount = '股息金額必須大於零'
  } else {
    if (!(Number(form.value.shares) > 0)) result.shares = '股數必須大於零'
    if (!(Number(form.value.price) > 0)) result.price = '成交價格必須大於零'
    const selectedStock = props.stocks.find(stock => stock.id === form.value.stockId)
    if (form.value.type === 'Sell' && selectedStock && Number(form.value.shares) > selectedStock.shares) {
      result.shares = `可用股數不足，目前最多可賣 ${selectedStock.shares}`
    }
  }
  if (Number(form.value.fee) < 0) result.fee = '手續費不可為負數'
  if (Number(form.value.tax) < 0) result.tax = '交易稅不可為負數'
  return result
})

// 將既有 transaction 映射回可編輯欄位，或重設為新增交易預設值。
function resetForm(): void {
  const transaction = props.transaction
  if (!transaction) {
    form.value = createEmptyForm(props.stockId ?? props.stocks[0]?.id ?? null)
    return
  }

  form.value = {
    stockId: transaction.stockId,
    type: transaction.type === 'OpeningBalance' ? 'Buy' : transaction.type,
    tradeDate: transaction.tradeDate,
    shares: transaction.shares?.toString() ?? '',
    price: transaction.price?.toString() ?? '',
    fee: transaction.fee.toString(),
    tax: transaction.tax.toString(),
    cashAmount: transaction.cashAmount?.toString() ?? '',
    notes: transaction.notes ?? '',
  }
}

watch(
  () => [props.open, props.stockId, props.transaction?.id] as const,
  () => resetForm(),
  { immediate: true },
)

// 將表單值轉成 backend 交易 contract，並排除與交易型別衝突的欄位。
function buildRequest(): StockLedgerTransactionRequest {
  const isDividend = form.value.type === 'Dividend'
  return {
    stockId: form.value.stockId!,
    type: form.value.type,
    tradeDate: form.value.tradeDate,
    shares: isDividend ? null : Number(form.value.shares),
    price: isDividend ? null : Number(form.value.price),
    fee: Number(form.value.fee) || 0,
    tax: Number(form.value.tax) || 0,
    cashAmount: isDividend ? Number(form.value.cashAmount) : null,
    notes: form.value.notes.trim() || null,
  }
}

// 只有通過欄位驗證時才送出交易 mutation。
function submit(): void {
  if (props.loading || Object.keys(errors.value).length > 0 || props.transaction?.type === 'OpeningBalance') return
  emit('save', buildRequest())
}
</script>

<template>
  <Modal
    :open="props.open"
    :title="props.transaction ? '編輯交易' : '新增交易'"
    :close-disabled="props.loading"
    mobile-full-screen
    @update:open="emit('update:open', $event)"
  >
    <form data-testid="stock-transaction-form" class="space-y-4" @submit.prevent="submit">
      <div>
        <label for="transaction-stock" class="mb-1 block text-sm font-medium text-text-primary">股票</label>
        <select
          id="transaction-stock"
          v-model="form.stockId"
          data-testid="transaction-stock"
          :disabled="props.loading || !!props.transaction"
          class="min-h-11 w-full rounded-lg border border-border-strong bg-bg-card px-3 py-2 text-sm text-text-primary focus:border-accent-primary focus:outline-none focus:ring-2 focus:ring-focus-ring"
        >
          <option :value="null">請選擇股票</option>
          <option v-for="stock in props.stocks" :key="stock.id" :value="stock.id">{{ stock.symbol }} {{ stock.name }}</option>
        </select>
        <p v-if="errors.stockId" class="mt-1 text-xs text-color-expense-text">{{ errors.stockId }}</p>
      </div>

      <div>
        <label for="transaction-type" class="mb-1 block text-sm font-medium text-text-primary">交易類型</label>
        <select
          id="transaction-type"
          v-model="form.type"
          data-testid="transaction-type"
          :disabled="props.loading || !!props.transaction"
          class="min-h-11 w-full rounded-lg border border-border-strong bg-bg-card px-3 py-2 text-sm text-text-primary focus:border-accent-primary focus:outline-none focus:ring-2 focus:ring-focus-ring"
        >
          <option value="Buy">買入</option>
          <option value="Sell">賣出</option>
          <option value="Dividend">股息</option>
        </select>
      </div>

      <div>
        <label for="transaction-trade-date" class="mb-1 block text-sm font-medium text-text-primary">交易日期</label>
        <Input id="transaction-trade-date" v-model="form.tradeDate" data-testid="transaction-trade-date" type="date" :disabled="props.loading" :error="errors.tradeDate" />
      </div>

      <div v-if="form.type !== 'Dividend'" class="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <div>
          <label class="mb-1 block text-sm font-medium text-text-primary">股數</label>
          <Input v-model="form.shares" data-testid="transaction-shares" type="number" :min="0" step="0.0001" :disabled="props.loading" :error="errors.shares" />
        </div>
        <div>
          <label class="mb-1 block text-sm font-medium text-text-primary">成交價格</label>
          <Input v-model="form.price" data-testid="transaction-price" type="number" :min="0" step="0.01" :disabled="props.loading" :error="errors.price" />
        </div>
      </div>

      <div v-else>
        <label class="mb-1 block text-sm font-medium text-text-primary">股息金額</label>
        <Input v-model="form.cashAmount" data-testid="transaction-cash-amount" type="number" :min="0" step="0.01" :disabled="props.loading" :error="errors.cashAmount" />
      </div>

      <div class="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <div>
          <label class="mb-1 block text-sm font-medium text-text-primary">手續費</label>
          <Input v-model="form.fee" data-testid="transaction-fee" type="number" :min="0" step="0.01" :disabled="props.loading" :error="errors.fee" />
        </div>
        <div>
          <label class="mb-1 block text-sm font-medium text-text-primary">交易稅</label>
          <Input v-model="form.tax" data-testid="transaction-tax" type="number" :min="0" step="0.01" :disabled="props.loading" :error="errors.tax" />
        </div>
      </div>

      <div>
        <label for="transaction-notes" class="mb-1 block text-sm font-medium text-text-primary">備註</label>
        <Input id="transaction-notes" v-model="form.notes" data-testid="transaction-notes" placeholder="選填" :disabled="props.loading" />
      </div>

      <p v-if="props.transaction?.type === 'OpeningBalance'" class="text-sm text-color-warning-text">期初部位由 Ledger 初始化管理，不能從一般交易表單修改。</p>
      <p v-if="props.errorMessage" data-testid="transaction-server-error" class="text-sm text-color-expense-text">{{ props.errorMessage }}</p>

      <div class="flex justify-end gap-3 pt-2">
        <Button type="button" variant="ghost" :disabled="props.loading" @click="emit('update:open', false)">取消</Button>
        <Button data-testid="transaction-save" type="submit" :loading="props.loading" :disabled="props.transaction?.type === 'OpeningBalance'">儲存交易</Button>
      </div>
    </form>
  </Modal>
</template>
