<script setup lang="ts">
import { computed } from 'vue'
import type { SnapshotBatch } from '../../types'
import Modal from '../ui/Modal.vue'
import { formatMoney, formatShares } from '../../utils/format'
import { formatStockInstrumentType } from '../../utils/stock'
import { maskSnapshotAccountNumber } from '../../utils/snapshot'
import { useTimeZone } from '../../composables/useTimeZone'

const props = defineProps<{
  open: boolean
  snapshot: SnapshotBatch | null
}>()
const emit = defineEmits<{ 'update:open': [value: boolean] }>()
const timeZone = useTimeZone()

// Exposes the nullable selected snapshot to the template.
const snapshot = computed(() => props.snapshot)

// Calculates aggregate stock gain or loss from the selected historical snapshot.
const stockGainLoss = computed(() => {
  if (!snapshot.value) return 0
  return snapshot.value.totalStockValue - snapshot.value.totalStockCost
})

// Formats a snapshot timestamp using the configured application time zone.
function formatDate(date: string): string {
  return timeZone.formatDateTime(date)
}

// Adds an explicit plus sign to positive monetary changes.
function formatGainLoss(value: number): string {
  return value > 0 ? `+${formatMoney(value)}` : formatMoney(value)
}

// Selects semantic text colors for positive, negative, and neutral changes.
function gainLossClass(value: number): string {
  if (value > 0) return 'text-emerald-600 dark:text-emerald-400'
  if (value < 0) return 'text-red-600 dark:text-red-400'
  return 'text-text-secondary'
}
</script>

<template>
  <Modal
    v-if="snapshot"
    :open="props.open"
    :title="snapshot?.name || '快照明細'"
    size="xl"
    mobile-full-screen
    scroll-body
    @update:open="emit('update:open', $event)"
  >
    <div class="min-w-0 space-y-6">
      <div class="min-w-0 border-b border-border-default pb-4">
        <p class="text-sm text-text-secondary">快照日期</p>
        <p class="mt-1 text-base font-medium text-text-primary break-words">{{ formatDate(snapshot.snapshotDate) }}</p>
        <p v-if="snapshot.notes?.trim()" class="mt-3 text-sm text-text-secondary whitespace-pre-wrap break-words">
          {{ snapshot.notes.trim() }}
        </p>
      </div>

      <div class="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
        <div class="min-w-0 rounded-xl border border-border-default bg-bg-card p-4">
          <p class="text-sm text-text-secondary">總淨值</p>
          <p class="mt-2 break-words text-3xl font-bold text-text-primary">{{ formatMoney(snapshot.totalNetWorth) }}</p>
        </div>

        <div class="min-w-0 rounded-xl border border-border-default bg-bg-card p-4">
          <p class="text-sm text-text-secondary">銀行總額</p>
          <p class="mt-2 break-words text-xl font-semibold text-text-primary">{{ formatMoney(snapshot.totalBankBalance) }}</p>
        </div>

        <div class="min-w-0 rounded-xl border border-border-default bg-bg-card p-4 sm:col-span-2 lg:col-span-1">
          <p class="text-sm text-text-secondary">股票資產摘要</p>
          <dl class="mt-3 space-y-2 text-sm">
            <div class="flex min-w-0 items-baseline justify-between gap-3">
              <dt class="min-w-0 text-text-secondary">預估賣出淨值</dt>
              <dd class="min-w-0 break-words text-right font-semibold text-text-primary">{{ formatMoney(snapshot.totalStockValue) }}</dd>
            </div>
            <div class="flex min-w-0 items-baseline justify-between gap-3">
              <dt class="min-w-0 text-text-secondary">股票總成本</dt>
              <dd class="min-w-0 break-words text-right font-semibold text-text-primary">{{ formatMoney(snapshot.totalStockCost) }}</dd>
            </div>
            <div class="flex min-w-0 items-baseline justify-between gap-3">
              <dt class="min-w-0 text-text-secondary">合計損益</dt>
              <dd class="min-w-0 break-words text-right font-semibold" :class="gainLossClass(stockGainLoss)">
                {{ formatGainLoss(stockGainLoss) }}
              </dd>
            </div>
          </dl>
        </div>
      </div>

      <section aria-labelledby="snapshot-bank-details-heading" class="min-w-0 space-y-3">
        <h2 id="snapshot-bank-details-heading" class="text-base font-semibold text-text-primary">
          銀行明細（{{ snapshot.bankDetails.length }}）
        </h2>

        <div v-if="snapshot.bankDetails.length > 0">
          <div class="hidden lg:block">
            <table class="w-full table-fixed border-collapse text-left">
              <thead>
                <tr class="border-b border-border-default text-sm text-text-secondary">
                  <th class="w-[28%] px-3 py-3 font-medium">銀行名稱</th>
                  <th class="w-[28%] px-3 py-3 font-medium">帳號</th>
                  <th class="w-[24%] px-3 py-3 font-medium">帳戶類型</th>
                  <th class="w-[20%] px-3 py-3 text-right font-medium">餘額</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="(bank, bankIndex) in snapshot.bankDetails" :key="`${bank.bankName}-${bankIndex}`" class="border-b border-border-default last:border-b-0">
                  <td class="min-w-0 break-words px-3 py-3 text-sm text-text-primary">{{ bank.bankName }}</td>
                  <td class="min-w-0 break-all px-3 py-3 font-mono text-sm text-text-secondary">{{ maskSnapshotAccountNumber(bank.accountNumber) }}</td>
                  <td class="min-w-0 break-words px-3 py-3 text-sm text-text-secondary">{{ bank.accountType || '未提供' }}</td>
                  <td class="min-w-0 break-all px-3 py-3 text-right text-sm font-medium text-text-primary">{{ formatMoney(bank.balance) }}</td>
                </tr>
              </tbody>
            </table>
          </div>

          <div class="space-y-3 lg:hidden">
            <article v-for="(bank, bankIndex) in snapshot.bankDetails" :key="`${bank.bankName}-${bankIndex}`" class="min-w-0 rounded-xl border border-border-default p-4">
              <div class="min-w-0">
                <p class="text-xs text-text-secondary">銀行名稱</p>
                <p class="mt-1 break-words font-medium text-text-primary">{{ bank.bankName }}</p>
              </div>
              <dl class="mt-4 grid min-w-0 grid-cols-1 gap-3 sm:grid-cols-3">
                <div class="min-w-0">
                  <dt class="text-xs text-text-secondary">帳號</dt>
                  <dd class="mt-1 break-all font-mono text-sm text-text-primary">{{ maskSnapshotAccountNumber(bank.accountNumber) }}</dd>
                </div>
                <div class="min-w-0">
                  <dt class="text-xs text-text-secondary">帳戶類型</dt>
                  <dd class="mt-1 break-words text-sm text-text-primary">{{ bank.accountType || '未提供' }}</dd>
                </div>
                <div class="min-w-0">
                  <dt class="text-xs text-text-secondary">餘額</dt>
                  <dd class="mt-1 min-w-0 break-all whitespace-normal text-sm font-medium text-text-primary">{{ formatMoney(bank.balance) }}</dd>
                </div>
              </dl>
            </article>
          </div>
        </div>
        <div v-else class="rounded-xl border border-dashed border-border-default px-4 py-6 text-center text-sm text-text-tertiary">
          尚無銀行明細
        </div>
      </section>

      <section aria-labelledby="snapshot-stock-details-heading" class="min-w-0 space-y-3">
        <h2 id="snapshot-stock-details-heading" class="text-base font-semibold text-text-primary">
          股票明細（{{ snapshot.stockDetails.length }}）
        </h2>

        <div v-if="snapshot.stockDetails.length > 0">
          <div class="hidden overflow-x-auto lg:block">
            <table class="w-full min-w-[900px] table-fixed border-collapse text-left">
              <thead>
                <tr class="border-b border-border-default text-sm text-text-secondary">
                  <th class="w-[18%] px-3 py-3 font-medium">名稱</th>
                  <th class="w-[10%] px-3 py-3 font-medium">代號</th>
                  <th class="w-[12%] px-3 py-3 font-medium">商品類型</th>
                  <th class="w-[10%] px-3 py-3 text-right font-medium">持有股數</th>
                  <th class="w-[12%] px-3 py-3 text-right font-medium">買入價</th>
                  <th class="w-[12%] px-3 py-3 text-right font-medium">現價</th>
                  <th class="w-[14%] px-3 py-3 text-right font-medium">預估賣出淨值</th>
                  <th class="w-[12%] px-3 py-3 text-right font-medium">預估損益</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="(stock, stockIndex) in snapshot.stockDetails" :key="`${stock.symbol}-${stock.name}-${stockIndex}`" class="border-b border-border-default last:border-b-0">
                  <td class="min-w-0 break-words px-3 py-3 text-sm font-medium text-text-primary">{{ stock.name }}</td>
                  <td class="min-w-0 break-all px-3 py-3 font-mono text-sm text-text-secondary">{{ stock.symbol }}</td>
                  <td class="min-w-0 break-words px-3 py-3 text-sm text-text-secondary">{{ formatStockInstrumentType(stock.instrumentType) }}</td>
                  <td class="px-3 py-3 text-right text-sm text-text-primary whitespace-nowrap">{{ formatShares(stock.shares) }}</td>
                  <td class="px-3 py-3 text-right text-sm text-text-primary whitespace-nowrap">{{ formatMoney(stock.buyPrice) }}</td>
                  <td class="px-3 py-3 text-right text-sm text-text-primary whitespace-nowrap">{{ formatMoney(stock.currentPrice) }}</td>
                  <td class="px-3 py-3 text-right text-sm font-medium text-text-primary whitespace-nowrap">{{ formatMoney(stock.marketValue) }}</td>
                  <td class="px-3 py-3 text-right text-sm font-medium whitespace-nowrap" :class="gainLossClass(stock.gainLoss)">
                    {{ formatGainLoss(stock.gainLoss) }}
                  </td>
                </tr>
              </tbody>
            </table>
          </div>

          <div class="space-y-3 lg:hidden">
            <article v-for="(stock, stockIndex) in snapshot.stockDetails" :key="`${stock.symbol}-${stock.name}-${stockIndex}`" class="min-w-0 rounded-xl border border-border-default p-4">
              <div class="min-w-0">
                <p class="break-words font-medium text-text-primary">{{ stock.name }}</p>
                <p class="mt-1 break-all font-mono text-sm text-text-secondary">{{ stock.symbol }}</p>
                <p class="mt-1 break-words text-xs text-text-secondary">{{ formatStockInstrumentType(stock.instrumentType) }}</p>
              </div>
              <dl class="mt-4 grid min-w-0 grid-cols-2 gap-x-4 gap-y-3 text-sm">
                <div class="min-w-0">
                  <dt class="text-xs text-text-secondary">持有股數</dt>
                  <dd class="mt-1 break-words text-text-primary">{{ formatShares(stock.shares) }}</dd>
                </div>
                <div class="min-w-0">
                  <dt class="text-xs text-text-secondary">買入價</dt>
                  <dd class="mt-1 break-words text-text-primary">{{ formatMoney(stock.buyPrice) }}</dd>
                </div>
                <div class="min-w-0">
                  <dt class="text-xs text-text-secondary">現價</dt>
                  <dd class="mt-1 break-words text-text-primary">{{ formatMoney(stock.currentPrice) }}</dd>
                </div>
                <div class="min-w-0">
                  <dt class="text-xs text-text-secondary">預估賣出淨值</dt>
                  <dd class="mt-1 break-words font-medium text-text-primary">{{ formatMoney(stock.marketValue) }}</dd>
                </div>
                <div class="min-w-0 col-span-2">
                  <dt class="text-xs text-text-secondary">預估損益</dt>
                  <dd class="mt-1 break-words font-medium" :class="gainLossClass(stock.gainLoss)">{{ formatGainLoss(stock.gainLoss) }}</dd>
                </div>
              </dl>
            </article>
          </div>
        </div>
        <div v-else class="rounded-xl border border-dashed border-border-default px-4 py-6 text-center text-sm text-text-tertiary">
          尚無股票明細
        </div>
      </section>
    </div>
  </Modal>
</template>
