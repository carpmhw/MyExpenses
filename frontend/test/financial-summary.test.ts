import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
import test from 'node:test'

const testDirectory = dirname(fileURLToPath(import.meta.url))
const sourceDirectory = join(testDirectory, '..', 'src')

// Reads a source module so the test can protect the financial-summary data boundary without a browser runtime.
function readSource(relativePath: string): string {
  return readFileSync(join(sourceDirectory, relativePath), 'utf8')
}

test('dashboard consumes complete dashboard summary instead of reducing recent rows', () => {
  const dashboard = readSource('pages/dashboard/index.vue')
  const footer = dashboard.split('<!-- Complete-period summary footer -->')[1]?.split('</template>')[0] ?? ''

  assert.match(dashboard, /api\.reports\.dashboardSummary/)
  assert.match(dashboard, /dashboardSummary\.value\??\.totalWithdrawals/)
  assert.match(dashboard, /提款合計/)
  assert.match(dashboard, /檢視全部/)
  assert.doesNotMatch(dashboard, /const totalWithdrawals = computed\(\(\) =>[\s\S]*reduce\(/)
  assert.match(footer, /<Icon name="TrendingDown"/)
  assert.match(footer, /<Icon name="Receipt"/)
  assert.match(footer, /<Icon name="Wallet"/)
  assert.match(footer, /<Icon name="CreditCard"/)
  assert.match(footer, /formatSummaryAmount\(totalWithdrawals\)/)
  assert.match(footer, /formatSummaryAmount\(totalExpenses\)/)
  assert.match(footer, /formatSummaryAmount\(disposableBalance\)/)
  assert.match(footer, /formatSummaryAmount\(installmentMonthlyDue\)/)
})

test('list pages render server-provided summaries', () => {
  assert.match(readSource('pages/expenses/index.vue'), /transactionQuery\.data\.value\?\.summary/)
  assert.match(readSource('pages/withdrawals/index.vue'), /withdrawalQuery\.data\.value\?\.summary/)
  assert.match(readSource('pages/installments/index.vue'), /installmentListQuery\.data\.value\?\.summary/)
})

test('reports consumes actual net-worth trend points without synthetic current-value steps', () => {
  const reports = readSource('pages/reports/index.vue')

  assert.match(reports, /api\.reports\.netWorthTrend/)
  assert.match(reports, /尚無完整淨值歷史/)
  assert.doesNotMatch(reports, /const step = current \/ 6/)
  assert.doesNotMatch(reports, /netWorthTrendLabels\.value\.map\(\(_, i\) => step \* \(i \+ 1\)\)/)
})
