<script setup lang="ts">
import type { StockTransactionListItem } from '../../types'
import Card from '../ui/Card.vue'
import Button from '../ui/Button.vue'
import { computed } from 'vue'
import { formatMoney, formatShares } from '../../utils/format'

const props = withDefaults(defineProps<{
  items: StockTransactionListItem[]
  loading: boolean
  total: number
  hasStocks: boolean
  page: number
  pageSize: number
}>(), {
  page: 1,
  pageSize: 20,
})

const emit = defineEmits<{
  create: []
  edit: [item: StockTransactionListItem]
  delete: [id: number]
  previous: []
  next: []
}>()

const totalPages = computed(() => Math.max(1, Math.ceil(props.total / props.pageSize)))

// 將 Ledger transaction type 轉成股票頁可讀的繁體中文。
function formatTransactionType(type: StockTransactionListItem['type']): string {
  return {
    OpeningBalance: '期初部位',
    Buy: '買入',
    Sell: '賣出',
    Dividend: '股息',
  }[type]
}
</script>

<template>
  <Card>
    <div class="mb-4 flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
      <div>
        <h2 class="text-base font-semibold text-text-primary">交易紀錄</h2>
        <p class="mt-1 text-xs text-text-secondary">所有欄位由 Ledger replay 計算，列表順序不影響計算順序。</p>
      </div>
      <Button v-if="props.hasStocks" data-testid="ledger-new-transaction" @click="emit('create')">+ 新增交易</Button>
    </div>
    <div v-if="props.loading" role="status" class="py-8 text-center text-text-tertiary">載入中...</div>
    <div v-else-if="props.items.length === 0" role="status" class="py-8 text-center text-text-tertiary">尚無交易紀錄</div>
    <div v-else class="overflow-x-auto">
      <table class="min-w-[980px] w-full text-sm">
        <thead>
          <tr class="border-b border-border-default">
            <th class="px-4 py-3 text-left font-medium text-text-secondary">日期</th>
            <th class="px-4 py-3 text-left font-medium text-text-secondary">標的</th>
            <th class="px-4 py-3 text-left font-medium text-text-secondary">類型</th>
            <th class="px-4 py-3 text-right font-medium text-text-secondary">股數／金額</th>
            <th class="px-4 py-3 text-right font-medium text-text-secondary">現金流</th>
                <th class="px-4 py-3 text-right font-medium text-text-secondary">已實現損益</th>
                <th class="px-4 py-3 text-right font-medium text-text-secondary">剩餘股數</th>
                <th class="px-4 py-3 text-left font-medium text-text-secondary">備註</th>
                <th class="px-4 py-3 text-right font-medium text-text-secondary">操作</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in props.items" :key="item.id" class="border-b border-border-default">
            <td class="whitespace-nowrap px-4 py-3 text-text-secondary">{{ item.tradeDate }}</td>
            <td class="px-4 py-3 font-medium text-text-primary">{{ item.stockName }} <span class="text-text-tertiary">({{ item.symbol }})</span></td>
            <td class="whitespace-nowrap px-4 py-3 text-text-secondary">{{ formatTransactionType(item.type) }}</td>
            <td class="px-4 py-3 text-right text-text-primary">{{ item.shares ?? item.cashAmount ?? 0 }}</td>
            <td class="px-4 py-3 text-right" :class="item.netCashFlow >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">{{ formatMoney(item.netCashFlow) }}</td>
            <td class="px-4 py-3 text-right" :class="item.realizedGainLoss >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">{{ formatMoney(item.realizedGainLoss) }}</td>
                <td class="px-4 py-3 text-right text-text-primary">{{ formatShares(item.remainingShares) }}</td>
                <td class="px-4 py-3 text-text-secondary">{{ item.notes || '－' }}</td>
                <td class="px-4 py-3">
                  <div class="flex justify-end gap-1">
                    <button :data-testid="`ledger-edit-${item.id}`" type="button" class="rounded-lg p-2 text-text-secondary transition-colors hover:bg-bg-raised hover:text-text-primary" title="編輯交易" @click="emit('edit', item)">
                      <span class="sr-only">編輯交易</span>
                      <span aria-hidden="true">編輯</span>
                    </button>
                    <button :data-testid="`ledger-delete-${item.id}`" type="button" class="rounded-lg p-2 text-color-expense-text transition-colors hover:bg-bg-raised" title="刪除交易" @click="emit('delete', item.id)">
                      <span class="sr-only">刪除交易</span>
                      <span aria-hidden="true">刪除</span>
                    </button>
                  </div>
                </td>
              </tr>
        </tbody>
      </table>
    </div>
    <div v-if="props.items.length > 0" class="mt-3 flex items-center justify-between gap-3 text-xs text-text-secondary">
      <span>共 {{ props.total }} 筆</span>
      <div class="flex items-center gap-2">
        <Button data-testid="ledger-page-prev" variant="ghost" :disabled="props.page <= 1" @click="emit('previous')">上一頁</Button>
        <span>{{ props.page }} / {{ totalPages }}</span>
        <Button data-testid="ledger-page-next" variant="ghost" :disabled="props.page >= totalPages" @click="emit('next')">下一頁</Button>
      </div>
    </div>
  </Card>
</template>
