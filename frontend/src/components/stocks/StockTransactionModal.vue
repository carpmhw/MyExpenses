<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ApiError, api, isRequestCancelled } from '../../api'
import type {
  EditableStockTransactionType,
  StockLedgerTransactionRequest,
  StockOption,
  StockOptionsStatus,
  StockTransactionCostEstimateRequest,
  StockTransactionCostEstimateResponse,
  StockTransactionListItem,
} from '../../types'
import Modal from '../ui/Modal.vue'
import Button from '../ui/Button.vue'
import Input from '../ui/Input.vue'
import { useTimeZone } from '../../composables/useTimeZone'
import { formatStockOption } from '../../utils/stock'

interface TransactionFormState {
  stockId: number | null
  type: EditableStockTransactionType
  tradeDate: string
  shares: string
  price: string
  fee: string
  tax: string
  cashAmount: string
  notes: string
}

type StockIdentity = Pick<StockOption, 'id' | 'name' | 'symbol' | 'broker'>
type CostMode = 'auto' | 'manual'
type EstimateStatus = 'idle' | 'loading' | 'ready' | 'error' | 'unsupported'

const props = withDefaults(defineProps<{
  open: boolean
  stocks: StockOption[]
  stockId: number | null
  transaction: StockTransactionListItem | null
  initialType?: EditableStockTransactionType
  stockOptionsStatus?: StockOptionsStatus
  stockIdentityFallback?: StockIdentity | null
  loading: boolean
  errorMessage?: string
}>(), {
  stockOptionsStatus: 'ready',
  stockIdentityFallback: null,
})

const emit = defineEmits<{
  'update:open': [value: boolean]
  save: [request: StockLedgerTransactionRequest]
}>()

const timeZone = useTimeZone()

// 建立空白交易表單，所有數值先保留字串以避免原生 input 的空值被轉成零。
// 建立新增交易的初始表單，保留快捷入口指定的交易型別。
function createEmptyForm(stockId: number | null, initialType: EditableStockTransactionType = 'Buy'): TransactionFormState {
  return {
    stockId,
    type: initialType,
    tradeDate: timeZone.getToday(),
    shares: '',
    price: '',
    fee: '',
    tax: '',
    cashAmount: '',
    notes: '',
  }
}

const form = ref<TransactionFormState>(createEmptyForm(
  props.stockId ?? props.stocks[0]?.id ?? null,
  props.initialType,
))

const costMode = ref<CostMode>(form.value.type === 'Dividend' ? 'manual' : 'auto')
const estimateStatus = ref<EstimateStatus>('idle')
const estimateResult = ref<StockTransactionCostEstimateResponse | null>(null)
const estimateRequestKey = ref<string | null>(null)
const estimateErrorMessage = ref('')
let estimateTimer: ReturnType<typeof setTimeout> | null = null
let estimateController: AbortController | null = null
let estimateGeneration = 0

// 將表單上的字串解析成有限正數，供買賣欄位與估算 payload 共用。
function parsePositive(value: string): number | null {
  if (!value.trim()) return null
  const parsed = Number(value)
  return Number.isFinite(parsed) && parsed > 0 ? parsed : null
}

// 將手動費稅欄位解析成有限非負數，保留明確輸入的零值。
function parseManualCost(value: string): number | null {
  if (!value.trim()) return null
  const parsed = Number(value)
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : null
}

// 建立手動費稅欄位的明確驗證訊息，區分空白、負數與非有限數值。
function manualCostError(value: string, label: string): string {
  if (!value.trim()) return `請輸入${label}`
  const parsed = Number(value)
  return Number.isFinite(parsed) && parsed < 0 ? `${label}不可為負數` : `請輸入${label}`
}

// 依目前表單輸入建立 backend 估算所需的 typed payload。
function getEstimatePayload(): StockTransactionCostEstimateRequest | null {
  if (form.value.type === 'Dividend' || !form.value.stockId) return null
  const shares = parsePositive(form.value.shares)
  const price = parsePositive(form.value.price)
  if (shares === null || price === null) return null
  return {
    stockId: form.value.stockId,
    type: form.value.type,
    shares,
    price,
  }
}

// 以 payload 的正規化值建立 request key，防止舊 response 套用到新 inputs。
function getEstimateInputKey(payload: StockTransactionCostEstimateRequest): string {
  return `${payload.stockId}:${payload.type}:${payload.shares}:${payload.price}`
}

// 判斷估算 request 是否仍屬於目前 Modal 與目前表單。
function isCurrentEstimate(generation: number, inputKey: string): boolean {
  return props.open
    && costMode.value === 'auto'
    && estimateGeneration === generation
    && getEstimatePayload() !== null
    && getEstimateInputKey(getEstimatePayload()!) === inputKey
}

// 取消現有 estimate request 並清除過期結果，必要時也清除 auto 顯示欄位。
function invalidateEstimate(clearCosts = true): void {
  estimateGeneration++
  if (estimateTimer) {
    clearTimeout(estimateTimer)
    estimateTimer = null
  }
  estimateController?.abort()
  estimateController = null
  estimateStatus.value = 'idle'
  estimateResult.value = null
  estimateRequestKey.value = null
  estimateErrorMessage.value = ''
  if (clearCosts) {
    form.value.fee = ''
    form.value.tax = ''
  }
}

// 執行目前 request 的 debounced backend 費稅估算並套用 latest-response guard。
async function requestEstimate(
  payload: StockTransactionCostEstimateRequest,
  generation: number,
  inputKey: string,
): Promise<void> {
  if (!isCurrentEstimate(generation, inputKey)) return
  const controller = new AbortController()
  estimateController = controller
  estimateStatus.value = 'loading'
  try {
    const result = await api.stocks.ledger.estimateCosts(payload, { signal: controller.signal })
    if (!isCurrentEstimate(generation, inputKey)) return
    estimateResult.value = result
    estimateRequestKey.value = inputKey
    form.value.fee = String(result.fee)
    form.value.tax = String(result.tax)
    estimateErrorMessage.value = ''
    estimateStatus.value = 'ready'
  } catch (error) {
    if (!isCurrentEstimate(generation, inputKey) || isRequestCancelled(error)) return
    estimateResult.value = null
    estimateRequestKey.value = null
    if (error instanceof ApiError && error.code === 'TransactionCostEstimationUnsupported') {
      estimateStatus.value = 'unsupported'
      estimateErrorMessage.value = ''
    } else {
      estimateStatus.value = 'error'
      estimateErrorMessage.value = error instanceof ApiError ? error.userMessage : '估算失敗，請稍後再試'
    }
  } finally {
    if (isCurrentEstimate(generation, inputKey) || estimateController === controller)
      estimateController = null
  }
}

// 只對有效的 Buy／Sell inputs 排程估算，並在新輸入出現時失效舊結果。
function scheduleEstimate(): void {
  invalidateEstimate()
  if (!props.open || costMode.value !== 'auto') return
  const payload = getEstimatePayload()
  if (!payload) return
  const inputKey = getEstimateInputKey(payload)
  const generation = estimateGeneration
  estimateTimer = setTimeout(() => {
    estimateTimer = null
    void requestEstimate(payload, generation, inputKey)
  }, 300)
}

// 依使用者選擇切換費稅來源，保留 ready estimate 切 manual 時的起始值。
function selectCostMode(mode: CostMode): void {
  if (mode === 'auto' && form.value.type === 'Dividend') return
  if (costMode.value === mode) return
  if (mode === 'manual') {
    const currentPayload = getEstimatePayload()
    const currentKey = currentPayload ? getEstimateInputKey(currentPayload) : null
    const canCopyEstimate = estimateStatus.value === 'ready'
      && estimateResult.value !== null
      && estimateRequestKey.value === currentKey
    if (canCopyEstimate) {
      form.value.fee = String(estimateResult.value!.fee)
      form.value.tax = String(estimateResult.value!.tax)
    }
    costMode.value = mode
    invalidateEstimate(!canCopyEstimate)
    return
  }
  costMode.value = mode
  scheduleEstimate()
}

const errors = computed(() => {
  const result: Record<string, string> = {}
  if (!form.value.stockId) result.stockId = '請選擇股票'
  if (!form.value.tradeDate) result.tradeDate = '請選擇交易日期'
  if (form.value.type === 'Dividend') {
    if (parsePositive(form.value.cashAmount) === null) result.cashAmount = '股息金額必須大於零'
  } else {
    if (parsePositive(form.value.shares) === null) result.shares = '股數必須大於零'
    if (parsePositive(form.value.price) === null) result.price = '成交價格必須大於零'
  }
  if (costMode.value === 'manual' || form.value.type === 'Dividend') {
    if (parseManualCost(form.value.fee) === null) result.fee = manualCostError(form.value.fee, '手續費')
    if (parseManualCost(form.value.tax) === null) result.tax = manualCostError(form.value.tax, '交易稅')
  }
  return result
})

const selectedStockIdentity = computed<StockIdentity | null>(() => {
  const selected = props.stocks.find(stock => stock.id === form.value.stockId)
  if (selected) {
    return {
      id: selected.id,
      name: selected.name,
      symbol: selected.symbol,
      broker: selected.broker,
    }
  }
  const transaction = props.transaction
  if (transaction && transaction.stockId === form.value.stockId) {
    return {
      id: transaction.stockId,
      name: transaction.stockName,
      symbol: transaction.symbol,
      broker: transaction.broker,
    }
  }
  return props.stockIdentityFallback?.id === form.value.stockId
    ? props.stockIdentityFallback
    : null
})

const currentHoldingShares = computed<number | null>(() => {
  if (props.stockOptionsStatus !== 'ready') return null
  return props.stocks.find(stock => stock.id === form.value.stockId)?.shares ?? null
})

const historicalRemainingShares = computed<number | null>(() => props.transaction?.remainingShares ?? null)

// 將既有 transaction 映射回可編輯欄位，或重設為新增交易預設值。
function resetForm(): void {
  invalidateEstimate()
  const transaction = props.transaction
  if (!transaction) {
    form.value = createEmptyForm(
      props.stockId ?? props.stocks[0]?.id ?? null,
      props.initialType,
    )
    costMode.value = form.value.type === 'Dividend' ? 'manual' : 'auto'
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
  costMode.value = 'manual'
}

watch(
  () => [props.open, props.stockId, props.transaction?.id, props.initialType] as const,
  () => resetForm(),
  { immediate: true },
)

watch(
  () => [form.value.stockId, form.value.type, form.value.shares, form.value.price] as const,
  () => {
    if (!props.open) return
    if (!props.transaction && form.value.type === 'Dividend') {
      if (costMode.value !== 'manual') selectCostMode('manual')
      return
    }
    if (costMode.value === 'auto') scheduleEstimate()
  },
)

watch(
  () => form.value.type,
  type => {
    if (props.transaction) return
    selectCostMode(type === 'Dividend' ? 'manual' : 'auto')
  },
)

watch(
  () => props.open,
  open => {
    if (!open) invalidateEstimate()
  },
)

onBeforeUnmount(() => invalidateEstimate())

// 將已驗證的費稅與表單值轉成 backend 交易 contract。
function buildRequest(costs: { fee: number; tax: number }): StockLedgerTransactionRequest {
  const isDividend = form.value.type === 'Dividend'
  return {
    stockId: form.value.stockId!,
    type: form.value.type,
    tradeDate: form.value.tradeDate,
    shares: isDividend ? null : parsePositive(form.value.shares),
    price: isDividend ? null : parsePositive(form.value.price),
    fee: costs.fee,
    tax: costs.tax,
    cashAmount: isDividend ? parsePositive(form.value.cashAmount) : null,
    notes: form.value.notes.trim() || null,
  }
}

// 只允許目前 inputs 的 ready estimate 或合法 manual 費稅進入 request。
function resolveCosts(): { fee: number; tax: number } | null {
  if (costMode.value === 'auto') {
    const payload = getEstimatePayload()
    const inputKey = payload ? getEstimateInputKey(payload) : null
    if (estimateStatus.value !== 'ready' || !estimateResult.value || estimateRequestKey.value !== inputKey)
      return null
    return { fee: estimateResult.value.fee, tax: estimateResult.value.tax }
  }
  const fee = parseManualCost(form.value.fee)
  const tax = parseManualCost(form.value.tax)
  return fee === null || tax === null ? null : { fee, tax }
}

// 只有通過欄位與費稅來源驗證時才送出交易 mutation。
function submit(): void {
  const costs = resolveCosts()
  if (props.loading || Object.keys(errors.value).length > 0 || !costs || props.transaction?.type === 'OpeningBalance') return
  emit('save', buildRequest(costs))
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
        <option v-if="selectedStockIdentity && !props.stocks.some(stock => stock.id === selectedStockIdentity?.id)" :value="selectedStockIdentity.id">
            {{ formatStockOption(selectedStockIdentity) }}
          </option>
          <option v-for="stock in props.stocks" :key="stock.id" :value="stock.id">{{ formatStockOption(stock) }}</option>
        </select>
        <p v-if="errors.stockId" class="mt-1 text-xs text-color-expense-text">{{ errors.stockId }}</p>
        <p v-if="selectedStockIdentity" data-testid="transaction-stock-summary" class="mt-1 text-xs text-text-secondary">
          {{ formatStockOption(selectedStockIdentity) }}
          <template v-if="currentHoldingShares !== null"> · 目前持有 {{ currentHoldingShares }} 股</template>
          <template v-else-if="props.stockOptionsStatus === 'loading'"> · 目前持股載入中</template>
          <template v-else-if="props.stockOptionsStatus === 'error'"> · 目前持股暫時無法取得</template>
          <template v-else> · 目前持股尚未確認</template>
        </p>
        <p v-if="historicalRemainingShares !== null" data-testid="transaction-historical-shares" class="mt-1 text-xs text-text-secondary">
          此交易完成後持股：{{ historicalRemainingShares }} 股
        </p>
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

      <div data-testid="transaction-cost-mode-controls" class="space-y-2">
        <div class="flex items-center justify-between gap-3">
          <span class="text-sm font-medium text-text-primary">費稅輸入方式</span>
          <div class="flex rounded-lg border border-border-default p-0.5">
            <button
              data-testid="transaction-cost-auto"
              type="button"
              :aria-pressed="costMode === 'auto'"
              :disabled="props.loading || form.type === 'Dividend'"
              class="rounded-md px-3 py-1.5 text-xs transition-colors"
              :class="costMode === 'auto' ? 'bg-bg-active text-text-primary' : 'text-text-secondary hover:bg-bg-raised'"
              @click="selectCostMode('auto')"
            >自動估算</button>
            <button
              data-testid="transaction-cost-manual"
              type="button"
              :aria-pressed="costMode === 'manual'"
              :disabled="props.loading"
              class="rounded-md px-3 py-1.5 text-xs transition-colors"
              :class="costMode === 'manual' ? 'bg-bg-active text-text-primary' : 'text-text-secondary hover:bg-bg-raised'"
              @click="selectCostMode('manual')"
            >手動輸入</button>
          </div>
        </div>
        <p v-if="costMode === 'auto' && estimateStatus === 'idle'" class="text-xs text-text-secondary">填入股數與成交價格後自動估算費稅。</p>
        <p v-else-if="costMode === 'auto' && estimateStatus === 'loading'" data-testid="transaction-estimate-loading" role="status" class="text-xs text-text-secondary">正在估算費稅，請稍候。</p>
        <p v-else-if="costMode === 'auto' && estimateStatus === 'ready'" data-testid="transaction-estimate-ready" class="text-xs text-text-secondary">系統估算值，實際金額以券商成交明細為準</p>
        <p v-else-if="costMode === 'auto' && estimateStatus === 'error'" data-testid="transaction-estimate-error" role="alert" class="text-xs text-color-expense-text">
          {{ estimateErrorMessage }}。請改用手動輸入。
          <button type="button" class="ml-1 underline" :disabled="props.loading" @click="scheduleEstimate">重新估算</button>
        </p>
        <p v-else-if="costMode === 'auto' && estimateStatus === 'unsupported'" data-testid="transaction-estimate-unsupported" role="alert" class="text-xs text-color-warning-text">此標的無法自動估算，請改用手動輸入。</p>
      </div>

      <div class="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <div>
          <label class="mb-1 block text-sm font-medium text-text-primary">手續費</label>
          <Input v-model="form.fee" data-testid="transaction-fee" type="number" :min="0" step="0.01" :disabled="props.loading" :readonly="costMode === 'auto'" :error="errors.fee" />
        </div>
        <div>
          <label class="mb-1 block text-sm font-medium text-text-primary">交易稅</label>
          <Input v-model="form.tax" data-testid="transaction-tax" type="number" :min="0" step="0.01" :disabled="props.loading" :readonly="costMode === 'auto'" :error="errors.tax" />
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
