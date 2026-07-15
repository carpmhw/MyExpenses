<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api } from '../../api'
import type { SnapshotCompareResult } from '../../types'
import Card from '../../components/ui/Card.vue'
import Button from '../../components/ui/Button.vue'
import { formatMoney } from '../../utils/format'
import { useTimeZone } from '../../composables/useTimeZone'

const route = useRoute()
const router = useRouter()
const timeZone = useTimeZone()

const result = ref<SnapshotCompareResult | null>(null)
const loading = ref(true)
const error = ref('')

function formatDate(dateStr: string) {
  return timeZone.formatDateTime(dateStr)
}

onMounted(async () => {
  const ids = route.query.ids as string
  if (!ids) {
    error.value = '缺少快照 ID'
    loading.value = false
    return
  }
  const parts = ids.split(',').map(Number)
  if (parts.length !== 2 || parts.some(isNaN)) {
    error.value = '無效的快照 ID'
    loading.value = false
    return
  }
  try {
    result.value = await api.snapshots.compare(parts[0], parts[1])
  } catch (e) {
    error.value = e instanceof Error ? e.message : '載入比對資料失敗'
  } finally {
    loading.value = false
  }
})
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

    <div v-if="loading" class="text-center py-12 text-text-tertiary">載入中...</div>
    <div v-else-if="error" class="text-center py-12 text-red-500">{{ error }}</div>
    <div v-else-if="result" class="space-y-6">
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
                <td class="py-3 px-3 text-text-primary font-medium">總淨值</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.netWorth.old) }}</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.netWorth.new) }}</td>
                <td class="py-3 px-3 text-right" :class="result.differences.netWorth.change >= 0 ? 'text-green-600' : 'text-red-600'">
                  {{ formatMoney(result.differences.netWorth.change) }}
                </td>
                <td class="py-3 px-3 text-right" :class="result.differences.netWorth.changePercent >= 0 ? 'text-green-600' : 'text-red-600'">
                  {{ result.differences.netWorth.changePercent }}%
                </td>
              </tr>
              <tr class="border-b border-border-default">
                <td class="py-3 px-3 text-text-primary">銀行總額</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.bankBalance.old) }}</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.bankBalance.new) }}</td>
                <td class="py-3 px-3 text-right" :class="result.differences.bankBalance.change >= 0 ? 'text-green-600' : 'text-red-600'">
                  {{ formatMoney(result.differences.bankBalance.change) }}
                </td>
                <td class="py-3 px-3 text-right" :class="result.differences.bankBalance.changePercent >= 0 ? 'text-green-600' : 'text-red-600'">
                  {{ result.differences.bankBalance.changePercent }}%
                </td>
              </tr>
              <tr class="border-b border-border-default">
                <td class="py-3 px-3 text-text-primary">股票預估賣出淨值</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.stockValue.old) }}</td>
                <td class="py-3 px-3 text-right text-text-primary">{{ formatMoney(result.differences.stockValue.new) }}</td>
                <td class="py-3 px-3 text-right" :class="result.differences.stockValue.change >= 0 ? 'text-green-600' : 'text-red-600'">
                  {{ formatMoney(result.differences.stockValue.change) }}
                </td>
                <td class="py-3 px-3 text-right" :class="result.differences.stockValue.changePercent >= 0 ? 'text-green-600' : 'text-red-600'">
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
                <td class="py-3 px-3 text-right" :class="b.change >= 0 ? 'text-green-600' : 'text-red-600'">{{ formatMoney(b.change) }}</td>
                <td class="py-3 px-3 text-right" :class="b.changePercent >= 0 ? 'text-green-600' : 'text-red-600'">{{ b.changePercent }}%</td>
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
                <td class="py-3 px-3 text-right" :class="s.change >= 0 ? 'text-green-600' : 'text-red-600'">{{ formatMoney(s.change) }}</td>
                <td class="py-3 px-3 text-right" :class="s.changePercent >= 0 ? 'text-green-600' : 'text-red-600'">{{ s.changePercent }}%</td>
              </tr>
            </tbody>
          </table>
        </div>
      </Card>
    </div>
  </div>
</template>
