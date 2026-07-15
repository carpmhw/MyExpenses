<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import Modal from '@/components/ui/Modal.vue'
import Input from '@/components/ui/Input.vue'
import Select from '@/components/ui/Select.vue'
import Button from '@/components/ui/Button.vue'
import Icon from '@/components/ui/Icon.vue'
import { useExchangeRates } from '@/composables/useExchangeRates'
import { useToast } from '@/composables/useToast'
import { useTimeZone } from '@/composables/useTimeZone'

const props = defineProps<{
  open: boolean
}>()

const emit = defineEmits<{
  'update:open': [value: boolean]
}>()

const { rates, updatedAt, loading, error, warning, fetchRates, convert, formatAmount, getCurrencyName, getCurrencySymbol } = useExchangeRates()
const toast = useToast()
const timeZone = useTimeZone()

const amount = ref<number>(1)
const fromCurrency = ref<string>('USD')
const toCurrency = ref<string>('TWD')
const result = ref<number | null>(null)

const currencyOptions = computed(() =>
  ['TWD', 'USD', 'JPY', 'CNY', 'HKD'].map(code => ({
    value: code,
    label: `${getCurrencySymbol(code)} ${getCurrencyName(code)} (${code})`,
  }))
)

/**
 * 當對話框開啟時自動載入匯率。
 */
watch(() => props.open, (isOpen) => {
  if (isOpen && Object.keys(rates.value).length === 0) {
    fetchRates()
  }
})

/**
 * 監聽金額或幣別變化，即時換算。
 */
watch([amount, fromCurrency, toCurrency], () => {
  if (amount.value > 0 && fromCurrency.value && toCurrency.value) {
    result.value = convert(amount.value, fromCurrency.value, toCurrency.value)
  } else {
    result.value = null
  }
}, { immediate: true })

/**
 * 互換來源與目標幣別。
 */
function swapCurrencies(): void {
  const temp = fromCurrency.value
  fromCurrency.value = toCurrency.value
  toCurrency.value = temp
}

/**
 * 複製換算結果到剪貼簿。
 */
async function copyResult(): Promise<void> {
  if (result.value === null) return

  try {
    await navigator.clipboard.writeText(formatAmount(result.value, toCurrency.value))
    toast.success('已複製到剪貼簿')
  } catch {
    toast.error('複製失敗，請手動複製')
  }
}

/**
 * 重新整理匯率。
 */
function refresh(): void {
  fetchRates()
}
</script>

<template>
  <Modal :open="open" title="💰 即時匯率換算" size="sm" @update:open="emit('update:open', $event)">
    <!-- 載入狀態 -->
    <div v-if="loading" class="flex flex-col items-center gap-3 py-10">
      <Icon name="Loader2" :size="32" class="animate-spin text-accent-primary" />
      <p class="text-sm text-text-secondary">正在載入匯率...</p>
    </div>

    <!-- 錯誤狀態 -->
    <div v-else-if="error" class="flex flex-col items-center gap-3 py-10">
      <Icon name="AlertCircle" :size="32" class="text-red-400" />
      <p class="text-sm text-red-400 text-center">{{ error }}</p>
      <Button variant="primary" @click="refresh">重試</Button>
    </div>

    <!-- 主內容 -->
    <template v-else>
      <!-- 警告訊息 -->
      <div v-if="warning" class="mb-4 flex items-center gap-2 rounded-lg bg-amber-500/10 px-3 py-2 text-sm text-amber-500">
        <Icon name="AlertTriangle" :size="16" />
        {{ warning }}
      </div>

      <div class="space-y-3">
        <!-- 金額輸入 -->
        <div>
          <label class="mb-1 block text-sm font-medium text-text-primary">金額</label>
          <Input v-model="amount" type="number" :min="0" step="0.01" placeholder="輸入金額" />
        </div>

        <!-- 幣別左右並排 -->
        <div class="grid grid-cols-[1fr_auto_1fr] gap-2 items-end">
          <div>
            <label class="mb-1 block text-sm font-medium text-text-primary">從</label>
            <Select v-model="fromCurrency" :options="currencyOptions" />
          </div>
          <button
            class="flex h-9 w-9 items-center justify-center rounded-full text-text-tertiary hover:bg-bg-secondary hover:text-text-primary transition-colors mb-0.5"
            title="互換幣別"
            @click="swapCurrencies"
          >
            <Icon name="ArrowLeftRight" :size="18" />
          </button>
          <div>
            <label class="mb-1 block text-sm font-medium text-text-primary">到</label>
            <Select v-model="toCurrency" :options="currencyOptions" />
          </div>
        </div>

        <!-- 換算結果卡片 -->
        <div v-if="result !== null" class="rounded-xl bg-gradient-to-br from-accent-primary/5 to-accent-primary/10 border border-accent-primary/20 p-4 text-center">
          <p class="text-xs font-medium text-accent-primary uppercase tracking-wider">換算結果</p>
          <p class="mt-2 text-3xl font-bold text-text-primary tracking-tight">{{ formatAmount(result, toCurrency) }}</p>
          <div class="mt-1.5 flex items-center justify-center gap-1 text-sm text-text-tertiary">
            <span>1 {{ getCurrencySymbol(fromCurrency) }}{{ fromCurrency }}</span>
            <span class="text-accent-primary font-medium">=</span>
            <span>{{ convert(1, fromCurrency, toCurrency)?.toFixed(4) ?? '-' }} {{ getCurrencySymbol(toCurrency) }}{{ toCurrency }}</span>
          </div>
          <div class="mt-4">
            <Button class="w-full" :disabled="result === null" @click="copyResult">📋 複製結果</Button>
          </div>
          <div class="mt-2 flex items-center justify-between text-xs text-text-tertiary">
            <button class="flex items-center gap-1 hover:text-text-primary transition-colors" :disabled="loading" @click="refresh">
              <Icon name="RefreshCw" :size="12" :class="loading ? 'animate-spin' : ''" />
              重新整理
            </button>
            <span v-if="updatedAt">{{ timeZone.formatDateTime(updatedAt) }}</span>
          </div>
        </div>
      </div>
    </template>
  </Modal>
</template>
