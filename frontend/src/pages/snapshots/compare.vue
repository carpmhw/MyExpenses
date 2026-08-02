<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api } from '../../api'
import type { SnapshotCompareResult } from '../../types'
import Card from '../../components/ui/Card.vue'
import Button from '../../components/ui/Button.vue'
import QueryState from '../../components/ui/QueryState.vue'
import { formatMoney } from '../../utils/format'
import { useTimeZone } from '../../composables/useTimeZone'
import { useAsyncQuery } from '../../composables/useAsyncQuery'

const route = useRoute()
const router = useRouter()
const timeZone = useTimeZone()

// Parses the two selected snapshot IDs from the route query string.
const compareIds = computed<[number, number] | null>(() => {
  const raw = route.query.ids
  if (typeof raw !== 'string' || !raw) return null
  const parts = raw.split(',').map(Number)
  return parts.length === 2 && parts.every(Number.isFinite) ? [parts[0], parts[1]] : null
})

const compareQuery = useAsyncQuery<SnapshotCompareResult>({
  key: () => ({ resource: 'snapshot-compare', ids: compareIds.value }),
  query: ({ signal }) => {
    if (!compareIds.value) return Promise.reject(new Error('缺少或無效的快照 ID'))
    return api.snapshots.compare(compareIds.value[0], compareIds.value[1], { signal })
  },
})

const result = computed(() => compareQuery.data.value ?? null)

// Returns a safe message for failed comparison requests.
function queryErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : '載入比對資料失敗，請重試。'
}

// Formats comparison timestamps using the configured application time zone.
function formatDate(dateStr: string) {
  return timeZone.formatDateTime(dateStr)
}
</script>

<template>
  <div class="p-4 lg:p-6">
    <div class="flex items-center justify-between mb-6">
      <div>
        <h1 class="text-2xl font-bold text-text-primary">快照比對</h1>
        <p class="text-xs text-text-secondary mt-1">Snapshot Comparison</p>
      </div>
      <Button variant="ghost" @click="router.push('/snapshots')">
        返回快照列表
      </Button>
    </div>

    <QueryState
      :status="compareQuery.status.value"
      :error-message="queryErrorMessage(compareQuery.error.value)"
      :retry="compareQuery.retry"
    >
    <div v-if="result" class="space-y-6">
      <div class="grid grid-cols-2 gap-4">
        <Card>
          <p class="text-xs text-text-secondary mb-1">{{ formatDate(result.snapshot1.date) }}</p>
          <p class="text-lg font-bold text-text-primary">{{ result.snapshot1.name }}</p>
        </Card>
        <Card>
          <p class="text-xs text-text-secondary mb-1">{{ formatDate(result.snapshot2.date) }}</p>
          <p class="text-lg font-bold text-text-primary">{{ result.snapshot2.name }}</p>
        </Card>
      </div>

      <Card>
        <h2 class="text-sm font-semibold text-text-primary mb-4">匯總差異</h2>
        <div class="overflow-x-auto">
          <table class="w-full text-sm">
            <thead>
              <tr class="border-b border-border-default">
                <th class="text-left py-2 px-3 text-text-secondary font-medium">項目</th>
                <th class="text-right py-2 px-3 text-text-secondary font-medium">舊值</th>
                <th class="text-right py-2 px-3 text-text-secondary font-medium">新值</th>
                <th class="text-right py-2 px-3 text-text-secondary font-medium">變動</th>
                <th class="text-right py-2 px-3 text-text-secondary font-medium">變動%</th>
              </tr>
            </thead>
            <tbody>
              <tr class="border-b border-border-default">
                 <td class="py-3 px-3 text-text-primary font-medium">
                   {{ result.differences.netWorthBasis === 'AssetsMinusLiabilities' ? '完整淨值' : '資產總額' }}
                 </td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.netWorth.old) }}</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.netWorth.new) }}</td>
                <td class="py-3 px-3 text-right" :class="result.differences.netWorth.change >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">
                  {{ formatMoney(result.differences.netWorth.change) }}
                </td>
                <td class="py-3 px-3 text-right" :class="result.differences.netWorth.changePercent >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">
                  {{ result.differences.netWorth.changePercent }}%
                </td>
              </tr>
              <tr class="border-b border-border-default">
                <td class="py-3 px-3 text-text-primary">總資產</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.assets.old) }}</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.assets.new) }}</td>
                <td class="py-3 px-3 text-right" :class="result.differences.assets.change >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">
                  {{ formatMoney(result.differences.assets.change) }}
                </td>
                <td class="py-3 px-3 text-right" :class="result.differences.assets.changePercent >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">
                  {{ result.differences.assets.changePercent }}%
                </td>
              </tr>
              <tr v-if="result.differences.liabilities" class="border-b border-border-default">
                <td class="py-3 px-3 text-text-primary">總負債</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.liabilities.old) }}</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.liabilities.new) }}</td>
                <td class="py-3 px-3 text-right" :class="result.differences.liabilities.change >= 0 ? 'text-color-expense-text' : 'text-color-income-text'">
                  {{ formatMoney(result.differences.liabilities.change) }}
                </td>
                <td class="py-3 px-3 text-right" :class="result.differences.liabilities.changePercent >= 0 ? 'text-color-expense-text' : 'text-color-income-text'">
                  {{ result.differences.liabilities.changePercent }}%
                </td>
              </tr>
              <tr class="border-b border-border-default">
                <td class="py-3 px-3 text-text-primary">銀行總額</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.bankBalance.old) }}</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.bankBalance.new) }}</td>
                <td class="py-3 px-3 text-right" :class="result.differences.bankBalance.change >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">
                  {{ formatMoney(result.differences.bankBalance.change) }}
                </td>
                <td class="py-3 px-3 text-right" :class="result.differences.bankBalance.changePercent >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">
                  {{ result.differences.bankBalance.changePercent }}%
                </td>
              </tr>
              <tr class="border-b border-border-default">
                <td class="py-3 px-3 text-text-primary">股票預估賣出淨值</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.stockValue.old) }}</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.stockValue.new) }}</td>
                <td class="py-3 px-3 text-right" :class="result.differences.stockValue.change >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">
                  {{ formatMoney(result.differences.stockValue.change) }}
                </td>
                <td class="py-3 px-3 text-right" :class="result.differences.stockValue.changePercent >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">
                  {{ result.differences.stockValue.changePercent }}%
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </Card>

      <Card v-if="result.differences.bankDetails.length > 0">
        <h2 class="text-sm font-semibold text-text-primary mb-4">銀行帳戶差異</h2>
        <div class="overflow-x-auto">
          <table class="w-full text-sm">
            <thead>
              <tr class="border-b border-border-default">
                <th class="text-left py-2 px-3 text-text-secondary font-medium">銀行</th>
                <th class="text-right py-2 px-3 text-text-secondary font-medium">舊餘額</th>
                <th class="text-right py-2 px-3 text-text-secondary font-medium">新餘額</th>
                <th class="text-right py-2 px-3 text-text-secondary font-medium">變動</th>
                <th class="text-right py-2 px-3 text-text-secondary font-medium">變動%</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="b in result.differences.bankDetails" :key="b.accountNumber" class="border-b border-border-default">
                <td class="py-3 px-3 text-text-primary">{{ b.bankName }}</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(b.oldBalance) }}</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(b.newBalance) }}</td>
                <td class="py-3 px-3 text-right" :class="b.change >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">{{ formatMoney(b.change) }}</td>
                <td class="py-3 px-3 text-right" :class="b.changePercent >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">{{ b.changePercent }}%</td>
              </tr>
            </tbody>
          </table>
        </div>
      </Card>

      <Card v-if="result.differences.stockDetails.length > 0">
        <h2 class="text-sm font-semibold text-text-primary mb-4">股票差異</h2>
        <div class="overflow-x-auto">
          <table class="w-full text-sm">
            <thead>
              <tr class="border-b border-border-default">
                <th class="text-left py-2 px-3 text-text-secondary font-medium">名稱</th>
                <th class="text-left py-2 px-3 text-text-secondary font-medium">代號</th>
                <th class="text-right py-2 px-3 text-text-secondary font-medium">舊預估賣出淨值</th>
                <th class="text-right py-2 px-3 text-text-secondary font-medium">新預估賣出淨值</th>
                <th class="text-right py-2 px-3 text-text-secondary font-medium">變動</th>
                <th class="text-right py-2 px-3 text-text-secondary font-medium">變動%</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="s in result.differences.stockDetails" :key="s.symbol" class="border-b border-border-default">
                <td class="py-3 px-3 text-text-primary">{{ s.name }}</td>
                <td class="py-3 px-3 text-text-secondary font-mono">{{ s.symbol }}</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(s.oldValue) }}</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(s.newValue) }}</td>
                <td class="py-3 px-3 text-right" :class="s.change >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">{{ formatMoney(s.change) }}</td>
                <td class="py-3 px-3 text-right" :class="s.changePercent >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">{{ s.changePercent }}%</td>
              </tr>
            </tbody>
          </table>
        </div>
      </Card>
    </div>
    </QueryState>
  </div>
</template>
