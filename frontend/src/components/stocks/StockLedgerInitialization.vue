<script setup lang="ts">
import { computed, ref } from 'vue'
import type { StockLedgerInitializationResponse, StockListItem } from '../../types'
import Card from '../ui/Card.vue'
import Button from '../ui/Button.vue'
import Input from '../ui/Input.vue'
import { useTimeZone } from '../../composables/useTimeZone'
import { formatMoney } from '../../utils/format'

const props = defineProps<{
  hasActiveHoldings: boolean
  loading: boolean
  response: StockLedgerInitializationResponse | null
  holdings?: Pick<StockListItem, 'shares' | 'currentPrice'>[]
}>()

const emit = defineEmits<{
  initialize: [baselineDate: string]
}>()

const timeZone = useTimeZone()
const baselineDate = ref(timeZone.getToday())

const activeHoldingCount = computed(() => props.holdings?.filter(stock => stock.shares > 0).length ?? 0)
const openingMarketValue = computed(() => (props.holdings ?? []).reduce((total, stock) => total + stock.shares * stock.currentPrice, 0))

// 將使用者選定的 baseline date 交給 page 呼叫 atomic initialization command。
function submitInitialization(): void {
  if (!baselineDate.value || props.loading) return
  emit('initialize', baselineDate.value)
}

// 將 backend blocking code 轉成可理解的繁體中文。
function formatBlockingReason(code: string): string {
  return {
    MissingBuyPrice: '缺少買入均價',
    MissingCurrentPrice: '缺少目前價格',
  }[code] ?? '資料不足'
}
</script>

<template>
  <Card v-if="props.hasActiveHoldings" data-testid="ledger-initialization" class="mb-6 border-color-info-border bg-color-info-bg">
    <div class="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
      <div class="max-w-2xl">
        <h2 class="text-base font-semibold text-text-primary">建立 Ledger 追蹤基準</h2>
        <p class="mt-1 text-sm text-text-secondary">系統不推測歷史買入日期；投資報酬只會從這個 baseline date 開始計算。</p>
        <p class="mt-1 text-xs text-text-tertiary">既有平均成本會保留作為損益成本基礎，期初市值只作為報酬追蹤的起點。</p>
        <p data-testid="ledger-baseline-summary" class="mt-2 text-xs text-text-secondary">將處理 {{ activeHoldingCount }} 檔，期初市值約 {{ formatMoney(openingMarketValue) }}</p>
        <details data-testid="ledger-initialization-help" class="mt-3 rounded-xl border border-color-info-border bg-bg-card px-3 py-2 text-xs text-text-secondary">
          <summary class="cursor-pointer font-medium text-text-primary">這是什麼？</summary>
          <ul class="mt-2 list-disc space-y-1 pl-5">
            <li>這會把尚未有交易紀錄的既有持股建立成 Ledger 的期初部位。</li>
            <li>投資報酬會從選定的 baseline date 開始計算，系統不會推測歷史買入日期。</li>
            <li>若缺少買入均價或目前價格，初始化會被阻擋，請先補齊股票資料。</li>
          </ul>
        </details>
      </div>
      <div class="flex flex-col gap-2 sm:flex-row sm:items-end">
        <Input v-model="baselineDate" data-testid="ledger-baseline-date" type="date" label="" :disabled="props.loading" />
        <Button data-testid="initialize-ledger" :loading="props.loading" @click="submitInitialization">開始初始化</Button>
      </div>
    </div>
    <div v-if="props.response" data-testid="ledger-initialization-result" class="mt-4 border-t border-color-info-border pt-3 text-sm text-text-secondary">
      <p>已建立 {{ props.response.initializedCount }} 檔，略過 {{ props.response.skippedCount }} 檔。</p>
      <div v-if="props.response.blockingCount > 0" class="mt-2 space-y-1 text-color-warning-text">
        <p>有 {{ props.response.blockingCount }} 檔需要先補正資料：</p>
        <p v-for="stock in props.response.blockingStocks" :key="stock.stockId">{{ stock.symbol }}：{{ formatBlockingReason(stock.code) }}</p>
      </div>
    </div>
  </Card>
</template>
