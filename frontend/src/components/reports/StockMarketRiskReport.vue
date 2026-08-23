<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '../../api'
import type {
  StockMarketRiskInstrument,
  StockMarketRiskMetric,
  StockMarketRiskReport as StockMarketRiskReportData,
  StockMarketRiskUnavailableReason,
} from '../../types'
import Card from '../ui/Card.vue'
import QueryState from '../ui/QueryState.vue'
import { useAsyncQuery } from '../../composables/useAsyncQuery'
import { formatMoney } from '../../utils/format'
import { formatStockMarket } from '../../utils/stock'

const periodMonths = ref<3 | 6 | 12>(12)

const riskQuery = useAsyncQuery<StockMarketRiskReportData>({
  key: () => ({ report: 'stock-market-risk', periodMonths: periodMonths.value }),
  query: ({ signal }) => api.reports.stockMarketRisk(
    { periodMonths: periodMonths.value },
    { signal },
  ),
  isEmpty: data => data.totalHoldingCount === 0,
  immediate: false,
})

const riskData = computed(() => riskQuery.data.value)
const rankingMaximum = computed(() => Math.max(
  ...(riskData.value?.volatilityRanking.map(item => item.annualizedVolatility) ?? [0]),
  0.0001,
))
// 防禦性依風險貢獻降冪排序，避免 API 傳入順序影響排名呈現。
const riskContributions = computed(() => [...(riskData.value?.riskContributions ?? [])]
  .sort((left, right) => right.contributionPercentage - left.contributionPercentage))

// 將查詢錯誤轉換成不暴露內部資訊的畫面訊息。
function queryErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : '載入市場風險失敗，請重試。'
}

// 將比例型統計轉成百分比文字，避免把不可用誤顯示為零。
function formatPercentage(value: number | null | undefined): string {
  return value === null || value === undefined ? '不可用' : `${(value * 100).toFixed(1)}%`
}

// 將可正可負的風險貢獻保留明確符號，避免正值與無方向比例混淆。
function formatSignedPercentage(value: number): string {
  return `${value > 0 ? '+' : ''}${(value * 100).toFixed(1)}%`
}

// 將年化波動度 metric 轉成可辨識的結果或原因。
function formatMetric(metric: StockMarketRiskMetric | undefined): string {
  if (!metric || metric.value === null)
    return `不可用：${formatReason(metric?.unavailableReason)}`
  return formatPercentage(metric.value)
}

// 為零波動的已知除零情境提供風險貢獻專用說明。
function riskContributionEmptyMessage(metric: StockMarketRiskMetric): string {
  if (metric.value === 0)
    return '組合波動度為 0，無法計算風險貢獻。'
  return `尚無可用風險貢獻；${formatReason(metric.unavailableReason)}。`
}

// 將後端 unavailable reason code 轉成使用者可理解的中文說明。
function formatReason(reason: StockMarketRiskUnavailableReason | null | undefined): string {
  const labels: Record<StockMarketRiskUnavailableReason, string> = {
    NoHoldings: '尚無目前持股',
    UnknownMarket: '市場待辨識',
    BlankSymbol: '代號空白',
    NonPositiveGrossValue: '毛市值不是正值',
    InsufficientHistory: '資料不足',
    NoEligibleInstruments: '沒有合格標的',
    CoverageBelowThreshold: '覆蓋不足',
    InsufficientCommonDates: '共同交易日不足',
    NotEnoughEligibleInstruments: '合格標的不滿兩檔',
    NonFiniteResult: '統計結果無效',
    InvalidPeriod: '觀察期無效',
  }
  return reason ? labels[reason] : '資料準備中'
}

// 將市場風險標的的排除原因轉成明細文字。
function exclusionReason(instrument: StockMarketRiskInstrument): string {
  return formatReason(instrument.exclusionReason)
}

// 將同步狀態映射成非敏感的畫面提示。
function syncStatusLabel(status: string): string {
  return status === 'Success' ? '同步成功' : status === 'AmbiguousMarket' ? '市場待選擇' : '同步有警告'
}

// 依相對波動度產生橫向排名列寬度。
function rankingWidth(value: number): string {
  return `${Math.max(4, Math.min(100, (value / rankingMaximum.value) * 100))}%`
}

// 依相關係數產生穩定的 heatmap 語意色階。
function correlationClass(value: number | null): string {
  if (value === null) return 'bg-bg-raised text-text-tertiary'
  if (value >= 0.75) return 'bg-color-expense-bg text-color-expense-text'
  if (value >= 0.25) return 'bg-color-warning-bg text-color-warning-text'
  if (value <= -0.75) return 'bg-color-info text-text-on-accent'
  return 'bg-bg-raised text-text-primary'
}

// 在分頁啟用後載入預設期間的本機市場風險資料。
function loadInitialData(): void {
  void riskQuery.refresh()
}

watch(periodMonths, () => {
  void riskQuery.refresh()
})

onMounted(loadInitialData)
</script>

<template>
  <div class="space-y-6">
    <div class="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
      <div>
        <h2 class="text-base font-semibold text-text-primary">市場風險情境</h2>
        <p class="mt-1 text-xs text-text-secondary">只使用本機歷史還原行情，不代表實際歷史績效、完整風險評等或買賣建議。</p>
      </div>
      <div class="flex rounded-lg bg-bg-raised p-1" role="group" aria-label="市場風險觀察期間">
        <button
          v-for="period in [3, 6, 12] as const"
          :key="period"
          :data-testid="`period-${period}`"
          type="button"
          class="rounded-md px-3 py-1.5 text-sm transition-colors cursor-pointer"
          :class="periodMonths === period ? 'bg-bg-active text-text-primary shadow-sm' : 'text-text-secondary hover:text-text-primary'"
          @click="periodMonths = period"
        >
          {{ period }} 個月
        </button>
      </div>
    </div>

    <div class="rounded-xl border border-color-info/30 bg-color-info/10 px-4 py-3 text-sm text-text-secondary">
      {{ riskData?.scenarioDescription ?? '目前持股歷史情境：以目前毛市值權重套用歷史還原日報酬。' }}
    </div>

    <QueryState
      :status="riskQuery.status.value"
      :error-message="queryErrorMessage(riskQuery.error.value)"
      :empty-message="'尚無目前持股，無法建立市場風險情境。'"
      :last-success-at="riskQuery.lastSuccessAt.value"
      :retry="riskQuery.retry"
    >
      <div v-if="riskData" class="space-y-6">
        <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-6">
          <Card>
            <p class="text-xs text-text-secondary">組合年化波動度</p>
            <p class="mt-2 text-xl font-bold text-text-primary">{{ formatMetric(riskData.portfolioAnnualizedVolatility) }}</p>
          </Card>
          <Card>
            <p class="text-xs text-text-secondary">組合最大回撤</p>
            <p class="mt-2 text-xl font-bold text-text-primary">{{ formatMetric(riskData.portfolioMaximumDrawdown) }}</p>
          </Card>
          <Card>
            <p class="text-xs text-text-secondary">行情市值覆蓋率</p>
            <p class="mt-2 text-xl font-bold text-text-primary">{{ formatMetric(riskData.eligibleMarketValueCoverageMetric) }}</p>
            <p class="mt-1 text-xs text-text-tertiary">門檻 {{ formatPercentage(riskData.coverageThreshold) }}</p>
          </Card>
          <Card>
            <p class="text-xs text-text-secondary">納入／排除標的</p>
            <p class="mt-2 text-xl font-bold text-text-primary">{{ riskData.includedInstruments.length }} / {{ riskData.excludedInstruments.length }}</p>
          </Card>
          <Card>
            <p class="text-xs text-text-secondary">共同交易日</p>
            <p class="mt-2 text-xl font-bold text-text-primary">{{ riskData.commonObservationCount }} 筆</p>
          </Card>
          <Card>
            <p class="text-xs text-text-secondary">資料截止日</p>
            <p class="mt-2 text-base font-bold text-text-primary">{{ riskData.dataCutoffDate ?? '尚無資料' }}</p>
          </Card>
        </div>

        <Card v-if="riskData.portfolioAnnualizedVolatility.value === null" class="border-color-warning-border">
          <p class="text-sm font-semibold text-color-warning-text">資料準備中／統計不可用</p>
          <p class="mt-1 text-sm text-text-secondary">{{ formatReason(riskData.portfolioAnnualizedVolatility.unavailableReason) }}。系統不會以零波動代表缺少資料。</p>
        </Card>

        <Card
          v-if="riskData.syncWarnings.length > 0"
          data-testid="market-sync-warning"
          title="同步有警告時保留最後成功資料；此狀態不會判定投資價值。"
          class="border-color-warning-border bg-color-warning-bg text-color-warning-text"
        >
          <div class="flex items-center justify-between gap-3">
            <h3 class="text-sm font-semibold text-color-warning-text">行情同步狀態</h3>
            <span class="text-xs text-text-tertiary">保留最後成功資料</span>
          </div>
          <div class="mt-3 grid gap-2">
            <div v-for="warning in riskData.syncWarnings" :key="`${warning.market}-${warning.symbol}`" class="rounded-lg bg-color-warning-bg px-3 py-2 text-sm text-color-warning-text">
              {{ warning.symbol }} · {{ formatStockMarket(warning.market) }} · {{ syncStatusLabel(warning.status) }}<span v-if="warning.safeMessage">：{{ warning.safeMessage }}</span>
            </div>
          </div>
        </Card>

        <div class="grid grid-cols-1 gap-6 xl:grid-cols-2">
          <Card>
            <div class="flex items-center justify-between gap-3">
              <h3 class="text-sm font-semibold text-text-primary">個別年化波動度排名</h3>
              <span class="text-xs text-text-tertiary">由高到低 · 相對比較</span>
            </div>
            <div v-if="riskData.volatilityRanking.length === 0" class="py-8 text-center text-sm text-text-tertiary">尚無合格標的波動度</div>
            <div v-else class="mt-4 space-y-3">
              <div v-for="item in riskData.volatilityRanking" :key="`${item.market}-${item.symbol}`" class="min-w-0">
                <div class="flex items-center justify-between gap-3 text-sm">
                  <span class="min-w-0 truncate text-text-primary">{{ item.name }} ({{ item.symbol }})</span>
                  <span class="shrink-0 font-semibold text-text-primary">{{ formatPercentage(item.annualizedVolatility) }}</span>
                </div>
                <div class="mt-1 h-2 rounded-full bg-bg-raised">
                  <div class="h-2 rounded-full bg-accent-primary" :style="{ width: rankingWidth(item.annualizedVolatility) }" />
                </div>
                <div class="mt-1 flex justify-between text-xs text-text-tertiary">
                  <span>{{ formatStockMarket(item.market) }} · {{ item.observations }} 筆</span>
                  <span>市值權重 {{ formatPercentage(item.weight) }}</span>
                </div>
              </div>
            </div>
          </Card>

          <Card>
            <div class="flex items-center justify-between gap-3">
              <h3 class="text-sm font-semibold text-text-primary">相關性矩陣</h3>
              <span class="text-xs text-text-tertiary">目前毛市值前 10 大</span>
            </div>
            <div v-if="riskData.correlationMatrix.unavailableReason" class="mt-4 rounded-lg bg-bg-raised px-3 py-4 text-center text-sm text-text-secondary">
              {{ formatReason(riskData.correlationMatrix.unavailableReason) }}，不補造相關係數。
            </div>
            <div v-else class="mt-4 max-w-full overflow-x-auto">
              <table class="min-w-[520px] text-xs">
                <thead>
                  <tr>
                    <th class="sticky left-0 bg-bg-card px-2 py-2 text-left font-medium text-text-secondary">標的</th>
                    <th v-for="label in riskData.correlationMatrix.labels" :key="`${label.market}-${label.symbol}`" class="px-2 py-2 text-center font-medium text-text-secondary" :title="label.name">
                      {{ label.symbol }}
                    </th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="(row, rowIndex) in riskData.correlationMatrix.values" :key="`${riskData.correlationMatrix.labels[rowIndex]?.market}-${riskData.correlationMatrix.labels[rowIndex]?.symbol}`" class="border-t border-border-default">
                    <th class="sticky left-0 bg-bg-card px-2 py-2 text-left font-medium text-text-primary">{{ riskData.correlationMatrix.labels[rowIndex]?.symbol }}</th>
                    <td v-for="(value, columnIndex) in row" :key="columnIndex" class="px-2 py-2 text-center font-mono" :class="correlationClass(value)">
                      {{ value === null ? '—' : value.toFixed(2) }}
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
            <p v-if="riskData.correlationMatrix.unavailableReason === null" class="mt-3 text-xs text-text-tertiary">
              共同觀測 {{ riskData.correlationMatrix.commonObservationCount }} 筆；數值只描述同一期間的歷史共同變動，不代表因果或買賣訊號。
            </p>
          </Card>
          <Card>
            <div class="flex items-center justify-between gap-3">
              <h3 class="text-sm font-semibold text-text-primary">風險貢獻排名</h3>
              <span class="text-xs text-text-tertiary">依貢獻由高到低</span>
            </div>
            <p class="mt-1 text-xs text-text-tertiary">以目前持股權重與所選期間共同歷史報酬估算；負值表示該期間分散效果。</p>
            <p v-if="riskContributions.length === 0" class="py-8 text-center text-sm text-text-tertiary">
              {{ riskContributionEmptyMessage(riskData.portfolioAnnualizedVolatility) }}
            </p>
            <div v-else class="mt-4 space-y-3">
              <div v-for="item in riskContributions" :key="`${item.market}-${item.symbol}-${item.name}`" class="flex items-start justify-between gap-3 text-sm">
                <div class="min-w-0">
                  <p class="truncate font-medium text-text-primary">{{ item.name }} ({{ item.symbol || '無代號' }})</p>
                  <p class="mt-1 text-xs text-text-tertiary">{{ formatStockMarket(item.market) }} · 市值權重 {{ formatPercentage(item.weight) }}</p>
                </div>
                <span class="shrink-0 font-semibold" :class="item.contributionPercentage < 0 ? 'text-color-info' : 'text-text-primary'">{{ formatSignedPercentage(item.contributionPercentage) }}</span>
              </div>
            </div>
          </Card>
        </div>

        <Card>
          <div class="flex items-center justify-between gap-3">
            <h3 class="text-sm font-semibold text-text-primary">排除標的</h3>
            <span class="text-xs text-text-tertiary">所有正毛市值部位都會列出</span>
          </div>
          <div v-if="riskData.excludedInstruments.length === 0" class="mt-4 text-sm text-text-tertiary">沒有排除標的。</div>
          <div v-else class="mt-4 overflow-x-auto">
            <table class="min-w-[620px] w-full text-sm">
              <thead>
                <tr class="border-b border-border-default">
                  <th class="py-2 text-left font-medium text-text-secondary">標的</th>
                  <th class="py-2 text-left font-medium text-text-secondary">市場</th>
                  <th class="py-2 text-right font-medium text-text-secondary">目前毛市值</th>
                  <th class="py-2 text-right font-medium text-text-secondary">可用筆數</th>
                  <th class="py-2 text-left font-medium text-text-secondary">排除原因</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="item in riskData.excludedInstruments" :key="`${item.market}-${item.symbol}-${item.name}`" class="border-b border-border-default">
                  <td class="py-2 text-text-primary">{{ item.name }} ({{ item.symbol || '無代號' }})</td>
                  <td class="py-2 text-text-secondary">{{ formatStockMarket(item.market) }}</td>
                  <td class="py-2 text-right text-text-primary">{{ formatMoney(item.grossMarketValue) }}</td>
                  <td class="py-2 text-right text-text-secondary">{{ item.observations }}</td>
                  <td class="py-2 text-color-warning-text">{{ exclusionReason(item) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </Card>
      </div>
    </QueryState>
  </div>
</template>
