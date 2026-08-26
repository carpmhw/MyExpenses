<script setup lang="ts">
import type { StockListItem } from '../../types'
import DataTable from '../ui/DataTable.vue'
import Icon from '../ui/Icon.vue'
import { formatMoney, formatShares } from '../../utils/format'
import { formatStockInstrumentType, formatStockMarket } from '../../utils/stock'

const props = defineProps<{
  items: StockListItem[]
  loading: boolean
  page: number
  pageSize: number
}>()

const emit = defineEmits<{
  edit: [item: StockListItem]
  buy: [item: StockListItem]
  sell: [item: StockListItem]
  delete: [id: number]
}>()

const columns = [
  { key: 'seq', label: '序號' },
  { key: 'name', label: '名稱' },
  { key: 'symbol', label: '代號' },
  { key: 'market', label: '市場' },
  { key: 'instrumentType', label: '商品類型' },
  { key: 'shares', label: '股數' },
  { key: 'buyPrice', label: '買入均價', align: 'right' as const },
  { key: 'currentPrice', label: '現價', align: 'right' as const },
  { key: 'pnl', label: '預估損益', align: 'right' as const },
  { key: 'broker', label: '券商' },
]

const freshnessColors: Record<'fresh' | 'warning' | 'stale', string> = {
  fresh: 'text-color-income-text',
  warning: 'text-color-warning-text',
  stale: 'text-color-expense-text',
}

// 依最近更新時間提供現價新鮮度的語意顏色。
function priceFreshness(lastUpdate: string | null): 'fresh' | 'warning' | 'stale' {
  if (!lastUpdate) return 'stale'
  const daysSinceUpdate = Math.floor((Date.now() - new Date(lastUpdate).getTime()) / (1000 * 60 * 60 * 24))
  if (daysSinceUpdate <= 1) return 'fresh'
  if (daysSinceUpdate <= 3) return 'warning'
  return 'stale'
}
</script>

<template>
  <DataTable :columns="columns" :loading="props.loading" :items="props.items">
    <template #empty>
      <div class="py-4 text-center text-text-tertiary">尚無股票資料</div>
    </template>
    <tr v-for="(item, idx) in props.items" :key="item.id" class="border-b border-border-default hover:bg-bg-raised">
      <td class="w-[60px] px-4 py-3 text-sm text-text-secondary">{{ (props.page - 1) * props.pageSize + idx + 1 }}</td>
      <td class="px-4 py-3 font-medium text-text-primary">{{ item.name }}</td>
      <td class="px-4 py-3 font-mono text-text-secondary">{{ item.symbol }}</td>
      <td class="whitespace-nowrap px-4 py-3 text-sm text-text-secondary">{{ formatStockMarket(item.market) }}</td>
      <td class="whitespace-nowrap px-4 py-3 text-sm text-text-secondary">{{ formatStockInstrumentType(item.instrumentType) }}</td>
      <td class="px-4 py-3 text-sm text-text-primary">{{ formatShares(item.shares) }}</td>
      <td class="px-4 py-3 text-right text-sm text-text-primary">{{ formatMoney(item.buyPrice) }}</td>
      <td class="px-4 py-3 text-right text-sm text-text-primary" :class="freshnessColors[priceFreshness(item.lastPriceUpdate)]">{{ formatMoney(item.currentPrice) }}</td>
      <td class="px-4 py-3 text-right text-sm font-semibold" :class="item.estimatedGainLoss >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">{{ formatMoney(item.estimatedGainLoss) }}</td>
      <td class="px-4 py-3 text-sm text-text-secondary">{{ item.broker || '－' }}</td>
      <td class="w-[80px] px-4 py-3">
        <div class="flex items-center gap-1">
          <button type="button" class="cursor-pointer rounded-lg p-1.5 text-text-secondary transition-colors hover:bg-bg-raised" @click="emit('edit', item)">
            <Icon name="pencil" :size="16" />
          </button>
          <button
            :data-testid="`stock-buy-${item.id}`"
            type="button"
            class="cursor-pointer rounded-lg px-2 py-1 text-xs font-medium text-color-income-text transition-colors hover:bg-bg-raised"
            @click="emit('buy', item)"
          >
            買入
          </button>
          <button
            :data-testid="`stock-sell-${item.id}`"
            type="button"
            class="cursor-pointer rounded-lg px-2 py-1 text-xs font-medium text-color-expense-text transition-colors hover:bg-bg-raised"
            @click="emit('sell', item)"
          >
            賣出
          </button>
          <button
            :data-testid="`stock-delete-${item.id}`"
            type="button"
            :disabled="item.hasLedger"
            :title="item.hasLedger ? '此股票已有交易紀錄，無法直接刪除' : '刪除股票'"
            class="rounded-lg p-1.5 text-color-expense-text transition-colors hover:bg-bg-raised disabled:cursor-not-allowed disabled:opacity-40"
            @click="emit('delete', item.id)"
          >
            <Icon name="trash-2" :size="16" />
          </button>
        </div>
      </td>
    </tr>
  </DataTable>
</template>
