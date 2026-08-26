export interface Category {
  id: number
  name: string
  type: 'Income' | 'Expense'
  icon: string
  color: string
  sortOrder: number
}

export interface Transaction {
  id: number
  type: 'Income' | 'Expense'
  amount: number
  date: string
  description: string | null
  notes: string | null
  categoryId: number
  paymentMethodId: number | null
  createdAt: string
  category: Category
  paymentMethod: PaymentMethod | null
}

export interface TransactionListSummary {
  totalAmount: number
  totalIncome: number
  totalExpense: number
  count: number
  dailyAverage: number
  maxAmount: number
}

export interface TransactionListResponse extends PaginatedResponse<Transaction> {
  summary: TransactionListSummary
}

export type InstallmentStatus = 'Active' | 'PaidOff'

export interface Installment {
  id: number
  transactionId: number | null
  cardId: number | null
  totalAmount: number
  periods: number
  perPeriod: number
  remainingPeriods: number
  status: InstallmentStatus
  purchaseDate: string
  createdAt: string
  description: string | null
  transaction: Transaction | null
  card: CreditCard | null
  payments: InstallmentPayment[]
}

export interface InstallmentPayment {
  id: number
  installmentId: number
  period: number
  amount: number
  paidDate: string | null
  dueDate: string | null
  isPaid: boolean
}

export interface InstallmentListSummary {
  totalCount: number
  activeCount: number
  dueAmount: number
  duePaymentCount: number
}

export interface InstallmentListResponse extends PaginatedResponse<Installment> {
  summary: InstallmentListSummary
}

export interface InstallmentCommandResponse {
  id: number
  transactionId: number | null
  cardId: number | null
  totalAmount: number
  periods: number
  perPeriod: number
  remainingPeriods: number
  status: InstallmentStatus
  purchaseDate: string
  createdAt: string
  description: string | null
  transaction: Pick<Transaction, 'id' | 'type' | 'amount' | 'date' | 'description' | 'notes' | 'categoryId' | 'paymentMethodId' | 'createdAt'> | null
  card: Pick<CreditCard, 'id' | 'bankName' | 'lastFourDigits' | 'cardNetwork' | 'statementDay' | 'dueDay' | 'creditLimit'> | null
  payments: InstallmentPayment[]
}

export interface InstallmentPurchaseRequest {
  transaction: {
    type: 'Expense'
    amount: number
    date?: string
    description: string
    notes?: string | null
    categoryId?: number
    categoryCode?: string
    category?: string
    paymentMethodId?: number
    paymentMethodCode?: string
    paymentMethod?: string
  }
  installment: {
    cardId: number
    periods: number
  }
}

export interface InstallmentPurchaseResponse {
  transaction: InstallmentCommandResponse['transaction']
  installment: InstallmentCommandResponse
}

export interface StandaloneInstallmentRequest {
  transactionId: number | null
  cardId: number | null
  totalAmount: number
  periods: number
  purchaseDate: string
  description: string | null
}

export interface UpdateInstallmentScheduleRequest {
  cardId?: number | null
  totalAmount?: number
  periods?: number
  purchaseDate?: string
  description?: string | null
}

export interface CreditCard {
  id: number
  bankName: string
  lastFourDigits: string
  cardNetwork: string | null
  statementDay: number
  dueDay: number
  creditLimit: number
  notes: string | null
  createdAt: string
  updatedAt: string
}

export interface CreditCardBill {
  id: number
  cardId: number
  period: string
  totalAmount: number
  paidAmount: number
  dueDate: string
  isPaid: boolean
  card: CreditCard
}

export interface BankAccount {
  id: number
  bankName: string
  accountNumber: string
  balance: number
  accountType: string
  createdAt: string
  updatedAt: string
}

export interface BankAccountListResponse extends PaginatedResponse<BankAccount> {
  totalBalance: number
}

export type StockInstrumentType = 'Stock' | 'StockEtf' | 'BondEtf'
export type StockMarket = 'Unknown' | 'Twse' | 'Tpex'

export interface Stock {
  id: number
  name: string
  symbol: string
  market: StockMarket
  instrumentType: StockInstrumentType
  shares: number
  buyPrice: number
  currentPrice: number
  broker: string | null
  lastPriceUpdate: string | null
}

export interface StockMetadataUpdateRequest {
  name: string
  market: StockMarket
  currentPrice: number
  lastPriceUpdate: string | null
}

export interface StockListItem extends Stock {
  grossMarketValue: number
  buyCommission: number
  sellCommission: number
  securitiesTransactionTax: number
  estimatedNetSellValue: number
  estimatedGainLoss: number
  hasLedger: boolean
}

export interface StockOption {
  id: number
  name: string
  symbol: string
  broker: string | null
  shares: number
  hasLedger: boolean
}

export type StockOptionsStatus = 'idle' | 'loading' | 'ready' | 'error'

export interface StockListResponse extends PaginatedResponse<StockListItem> {
  totalEstimatedNetSellValue: number
  totalEstimatedGainLoss: number
}

export type StockTransactionType = 'OpeningBalance' | 'Buy' | 'Sell' | 'Dividend'
export type EditableStockTransactionType = 'Buy' | 'Sell' | 'Dividend'

export interface StockTransaction {
  id: number
  stockId: number
  type: StockTransactionType
  tradeDate: string
  sequence: number
  shares: number | null
  price: number | null
  fee: number
  tax: number
  cashAmount: number | null
  openingMarketValue: number | null
  notes: string | null
  createdAtUtc: string
  updatedAtUtc: string
}

export interface StockTransactionListItem {
  id: number
  stockId: number
  stockName: string
  symbol: string
  market: StockMarket
  broker: string | null
  type: StockTransactionType
  tradeDate: string
  sequence: number
  shares: number | null
  price: number | null
  fee: number
  tax: number
  cashAmount: number | null
  openingMarketValue: number | null
  notes: string | null
  grossAmount: number
  netCashFlow: number
  allocatedCostBasis: number | null
  realizedGainLoss: number
  netDividend: number
  remainingShares: number
  remainingCostBasis: number
  executionAveragePrice: number
}

export interface StockTransactionListResponse {
  items: StockTransactionListItem[]
  total: number
  page: number
  pageSize: number
}

export interface StockLedgerTransactionRequest {
  stockId: number
  type: StockTransactionType
  tradeDate: string
  shares?: number | null
  price?: number | null
  fee?: number
  tax?: number
  cashAmount?: number | null
  openingMarketValue?: number | null
  notes?: string | null
}

export interface StockTransactionCostEstimateRequest {
  stockId: number
  type: 'Buy' | 'Sell'
  shares: number
  price: number
}

export interface StockTransactionCostEstimateResponse {
  grossAmount: number
  fee: number
  tax: number
}

export interface StockLedgerBlockingStock {
  stockId: number
  symbol: string
  reason: string
  code: string
  buyPrice: number
  currentPrice: number
}

export interface StockLedgerInitializationResponse {
  initializedCount: number
  skippedCount: number
  blockingCount: number
  totalCount: number
  blockingStocks: StockLedgerBlockingStock[]
}

export interface StockPositionRequest {
  name: string
  symbol: string
  market: StockMarket
  instrumentType: StockInstrumentType
  shares: number
  buyPrice: number
  currentPrice: number
  tradeDate: string
  initialTransactionType: 'Buy' | 'OpeningBalance'
  broker?: string | null
  fee?: number
  tax?: number
  openingMarketValue?: number | null
  notes?: string | null
}

export interface StockLedgerProjection {
  remainingShares: number
  remainingCostBasis: number
  executionAveragePrice: number
}

export interface StockLedgerResult {
  projection: StockLedgerProjection
  realizedGainLoss: number
  netDividendIncome: number
  entries: unknown[]
  remainingShares: number
  remainingCostBasis: number
  executionAveragePrice: number
}

export interface StockPositionResponse {
  stock: Stock
  transaction: StockTransaction
  replay: StockLedgerResult
}

export type StockPerformanceUnavailableReason =
  | 'None'
  | 'NoHoldings'
  | 'NoLedgerHistory'
  | 'IncompleteLedgerCoverage'
  | 'PeriodBeforeTrackingStart'
  | 'InsufficientCashFlows'
  | 'NoCashFlowSignChange'
  | 'MissingTerminalValue'
  | 'NoConvergence'
  | 'NonFiniteResult'
  | 'InsufficientHistoricalPrices'
  | 'ZeroDenominator'
  | 'InvalidPeriod'

export interface StockPerformanceMetric {
  value: number | null
  unavailableReason: StockPerformanceUnavailableReason
}

export interface StockPerformanceSummary {
  currentGrossMarketValue: number
  remainingCostBasis: number
  realizedGainLoss: number
  unrealizedGainLoss: number
  netDividendIncome: number
  totalGainLoss: number
}

export interface StockPerformanceDataQuality {
  activeInstrumentCount: number
  ledgerManagedInstrumentCount: number
  priceObservationCount: number
  priceCoverage: number
  trackingStartReason: StockPerformanceUnavailableReason
  hasIncompleteLedgerCoverage: boolean
}

export interface StockPerformanceMonthlyPoint {
  month: string
  endingMarketValue: number
  netContribution: number
  realizedGainLoss: number
  dividendIncome: number
  cumulativeTwr: number | null
}

export interface StockPerformanceInstrumentBreakdown {
  stockId: number
  name: string
  symbol: string
  market: StockMarket
  broker: string | null
  currentShares: number
  grossMarketValue: number
  remainingCostBasis: number
  realizedGainLoss: number
  unrealizedGainLoss: number
  dividendIncome: number
  totalGainLoss: number
  isClosed: boolean
}

export interface StockPerformanceReport {
  dateStart: string
  dateEnd: string
  trackingStartDate: string | null
  hasSyntheticOpeningBalances: boolean
  terminalValuationSource: string
  ledgerCoverage: StockPerformanceMetric
  summary: StockPerformanceSummary
  twr: StockPerformanceMetric
  xirr: StockPerformanceMetric
  monthlyPoints: StockPerformanceMonthlyPoint[]
  instrumentBreakdown: StockPerformanceInstrumentBreakdown[]
  dataQuality: StockPerformanceDataQuality
}

export interface StockStructureSummary {
  holdingCount: number
  totalEstimatedBuyCost: number
  totalGrossMarketValue: number
  totalEstimatedNetSellValue: number
  totalEstimatedGainLoss: number
  estimatedGainLossPercentage: number | null
}

export interface StockStructureInsight {
  code: string
  severity: 'Warning' | 'Info'
  message: string
  affectedName: string | null
  observedPercentage: number | null
  thresholdPercentage: number | null
  affectedCount: number | null
  amount: number | null
}

export interface StockStructureAllocation {
  key: string
  label: string
  value: number
  percentage: number | null
}

export interface StockStructureConcentration {
  top1Percentage: number | null
  top3Percentage: number | null
  top5Percentage: number | null
  hhi: number | null
  effectiveHoldingCount: number | null
}

export interface StockStructureDataQuality {
  holdingCount: number
  positivePriceCount: number
  missingLastPriceUpdateCount: number
  stalePriceCount: number
  positivePriceCoverage: number | null
  oldestLastPriceUpdateUtc: string | null
  latestLastPriceUpdateUtc: string | null
  staleAfterHours: number
  generatedAtUtc: string
}

export interface StockStructureHolding {
  id: number
  name: string
  symbol: string
  instrumentType: StockInstrumentType
  shares: number
  buyPrice: number
  currentPrice: number
  broker: string | null
  grossMarketValue: number
  buyCommission: number
  sellCommission: number
  securitiesTransactionTax: number
  estimatedBuyCost: number
  estimatedNetSellValue: number
  estimatedGainLoss: number
  allocationPercentage: number | null
}

export interface StockStructureReport {
  summary: StockStructureSummary
  insights: StockStructureInsight[]
  symbolAllocations: StockStructureAllocation[]
  instrumentTypeAllocations: StockStructureAllocation[]
  brokerAllocations: StockStructureAllocation[]
  marketAllocations: StockStructureAllocation[]
  concentration: StockStructureConcentration
  dataQuality: StockStructureDataQuality
  holdings: StockStructureHolding[]
  availableBrokers: string[]
  availableInstrumentTypes: StockInstrumentType[]
  generatedAt: string
}

export type StockMarketRiskUnavailableReason =
  | 'NoHoldings'
  | 'UnknownMarket'
  | 'BlankSymbol'
  | 'NonPositiveGrossValue'
  | 'InsufficientHistory'
  | 'NoEligibleInstruments'
  | 'CoverageBelowThreshold'
  | 'InsufficientCommonDates'
  | 'NotEnoughEligibleInstruments'
  | 'NonFiniteResult'
  | 'InvalidPeriod'

export type HistoricalPriceSyncStatus = 'Success' | 'ProviderError' | 'InvalidResponse' | 'NoData' | 'AmbiguousMarket'

export interface StockMarketRiskMetric {
  value: number | null
  unavailableReason: StockMarketRiskUnavailableReason | null
}

export interface StockMarketRiskInstrument {
  name: string
  symbol: string
  market: StockMarket
  grossMarketValue: number
  originalWeight: number
  renormalizedWeight: number
  observations: number
  annualizedVolatility: number | null
  exclusionReason: StockMarketRiskUnavailableReason | null
}

export interface StockMarketRiskVolatilityRanking {
  name: string
  symbol: string
  market: StockMarket
  grossMarketValue: number
  weight: number
  annualizedVolatility: number
  observations: number
}

export interface StockMarketRiskCorrelationLabel {
  name: string
  symbol: string
  market: StockMarket
}

export interface StockMarketRiskCorrelationMatrix {
  labels: StockMarketRiskCorrelationLabel[]
  values: (number | null)[][]
  commonObservationCount: number
  unavailableReason: StockMarketRiskUnavailableReason | null
}

export interface StockMarketRiskSyncWarning {
  symbol: string
  market: StockMarket
  status: HistoricalPriceSyncStatus
  safeMessage: string | null
  lastAttemptedAtUtc: string | null
  lastSucceededAtUtc: string | null
  latestTradingDate: string | null
}

export interface StockMarketRiskContribution {
  name: string
  symbol: string
  market: StockMarket
  grossMarketValue: number
  weight: number
  componentVolatilityContribution: number
  contributionPercentage: number
}

export interface StockMarketRiskReport {
  periodMonths: 3 | 6 | 12
  scenarioDescription: string
  calculationDate: string
  dataCutoffDate: string | null
  portfolioAnnualizedVolatility: StockMarketRiskMetric
  portfolioMaximumDrawdown: StockMarketRiskMetric
  eligibleMarketValueCoverage: number
  eligibleMarketValueCoverageMetric: StockMarketRiskMetric
  coverageThreshold: number
  commonObservationCount: number
  totalHoldingCount: number
  includedInstruments: StockMarketRiskInstrument[]
  excludedInstruments: StockMarketRiskInstrument[]
  volatilityRanking: StockMarketRiskVolatilityRanking[]
  correlationMatrix: StockMarketRiskCorrelationMatrix
  syncWarnings: StockMarketRiskSyncWarning[]
  riskContributions: StockMarketRiskContribution[]
}

export interface StockValueTrendPoint {
  month: string
  snapshotDate: string
  name: string
  totalStockValue: number
  basis: 'AssetsOnly' | 'AssetsMinusLiabilities'
}

export interface PaymentMethod {
  id: number
  name: string
  systemCode?: string | null
  icon: string
  sortOrder: number
  color: string
}

export interface Withdrawal {
  id: number
  amount: number
  date: string
  description: string | null
  bankAccountId: number
  bankAccount: BankAccount
}

export interface WithdrawalListSummary {
  totalAmount: number
  count: number
  averageAmount: number
  maxAmount: number
}

export interface WithdrawalListResponse extends PaginatedResponse<Withdrawal> {
  summary: WithdrawalListSummary
}

export interface MonthlyTrend {
  month: string
  income: number
  expense: number
}

export interface CategoryDistribution {
  categoryId: number
  categoryName: string
  color: string
  icon: string
  total: number
  percentage: number
}

export interface NetWorth {
  totalAssets: number
  totalLiabilities: number
  netWorth: number
  bankAccounts: { bankName: string; accountNumber: string; balance: number }[]
  stocks: { name: string; symbol: string; instrumentType: StockInstrumentType; shares: number; currentPrice: number; grossMarketValue: number; estimatedNetSellValue: number }[]
}

export interface ForecastPayment {
  cardBankName: string
  description: string | null
  period: number
  amount: number
  dueDate: string
}

export interface MonthlyForecast {
  month: string
  totalAmount: number
  payments: ForecastPayment[]
}

export interface MonthlySummary {
  totalIncome: number
  totalExpense: number
  totalBankBalance: number
}

export interface DashboardSummary {
  totalWithdrawals: number
  withdrawalCount: number
  totalExpenses: number
  expenseCount: number
  disposableBalance: number
  installmentDueAmount: number
  installmentDuePaymentCount: number
  activeInstallmentCount: number
  previousDisposableBalance: number
}

export interface BankDetail {
  bankName: string
  accountNumber: string
  accountType: string
  balance: number
}

export interface StockDetail {
  name: string
  symbol: string
  instrumentType: StockInstrumentType
  shares: number
  buyPrice: number
  currentPrice: number
  marketValue: number
  gainLoss: number
}

export interface SnapshotBatch {
  id: number
  name: string
  snapshotDate: string
  notes: string | null
  totalAssets: number
  totalLiabilities: number | null
  totalNetWorth: number
  netWorthBasis: 'AssetsOnly' | 'AssetsMinusLiabilities'
  totalBankBalance: number
  totalStockValue: number
  totalStockCost: number
  bankDetails: BankDetail[]
  stockDetails: StockDetail[]
}

export interface AutoSnapshotConfig {
  id: number
  isEnabled: boolean
  frequency: 'Daily' | 'Weekly' | 'Monthly'
  dayOfWeek: number | null
  dayOfMonth: number | null
  timeOfDay: string
  lastRunAt: string | null
}

export type ScheduledJobKey = 'AutomaticSnapshot' | 'StockPriceUpdate' | 'HistoricalMarketDataSync'
export type ScheduledJobExecutionStatus = 'Running' | 'Succeeded' | 'PartiallySucceeded' | 'Failed' | 'Canceled' | 'Interrupted'

export interface ScheduledJobExecutionSummary {
  id: number
  jobKey: ScheduledJobKey
  scheduledForUtc: string
  scheduleTimeZoneId: string
  scheduledLocalDate: string
  status: ScheduledJobExecutionStatus
  startedAtUtc: string
  completedAtUtc: string | null
  attemptCount: number
  targetCount: number | null
  succeededCount: number
  failedCount: number
  affectedCount: number
  resultCode: string | null
  safeMessage: string | null
}

export interface ScheduleOverviewItem {
  jobKey: ScheduledJobKey
  displayName: string
  configurationSource: string
  isEnabled: boolean
  frequencyDescription: string
  scheduleTimeZoneId: string
  nextRunAtUtc: string | null
  latestExecution: ScheduledJobExecutionSummary | null
}

export interface ScheduleExecutionQuery {
  jobKey?: ScheduledJobKey | string
  status?: ScheduledJobExecutionStatus | string
  dateStart?: string
  dateEnd?: string
  page?: number
  pageSize?: number
}

export interface ScheduleExecutionHistoryResponse extends PaginatedResponse<ScheduledJobExecutionSummary> {}

export interface TrendPoint {
  id: number
  date: string
  name: string
  totalAssets: number
  totalLiabilities: number | null
  totalNetWorth: number
  netWorthBasis: 'AssetsOnly' | 'AssetsMinusLiabilities'
  totalBankBalance: number
  totalStockValue: number
  totalStockCost: number
}

export interface SnapshotListResponse extends PaginatedResponse<SnapshotBatch> {}

export interface NetWorthTrendPoint {
  month: string
  snapshotDate: string
  name: string
  totalAssets: number
  totalLiabilities: number
  netWorth: number
}

export interface SnapshotDiff {
  old: number
  new: number
  change: number
  changePercent: number
}

export interface CompareBankDetail {
  bankName: string
  accountNumber: string
  oldBalance: number
  newBalance: number
  change: number
  changePercent: number
}

export interface CompareStockDetail {
  name: string
  symbol: string
  oldShares: number
  newShares: number
  oldPrice: number
  newPrice: number
  oldValue: number
  newValue: number
  change: number
  changePercent: number
}

export interface SnapshotCompareResult {
  snapshot1: {
    id: number
    date: string
    name: string
    totalAssets: number
    totalLiabilities: number | null
    totalNetWorth: number
    netWorthBasis: 'AssetsOnly' | 'AssetsMinusLiabilities'
    totalBankBalance: number
    totalStockValue: number
    totalStockCost: number
  }
  snapshot2: {
    id: number
    date: string
    name: string
    totalAssets: number
    totalLiabilities: number | null
    totalNetWorth: number
    netWorthBasis: 'AssetsOnly' | 'AssetsMinusLiabilities'
    totalBankBalance: number
    totalStockValue: number
    totalStockCost: number
  }
  differences: {
    netWorth: SnapshotDiff
    netWorthBasis: 'AssetsOnly' | 'AssetsMinusLiabilities'
    assets: SnapshotDiff
    liabilities: SnapshotDiff | null
    bankBalance: SnapshotDiff
    stockValue: SnapshotDiff
    bankDetails: CompareBankDetail[]
    stockDetails: CompareStockDetail[]
  }
}

export interface User {
  id: number
  email: string
  displayName: string
  isTwoFactorEnabled: boolean
}

export interface SystemTimeZoneSettings {
  timeZoneId: string
}

export interface ApiToken {
  id: number
  name: string
  prefix: string
  scopes: string[] | null
  createdAt: string
  lastUsedAt: string | null
  expiresAt: string | null
  isRevoked: boolean
}

export interface AuthResponse {
  token?: string
  requiresTwoFactor?: boolean
  tempToken?: string
  user?: User
}

export interface TwoFactorSetupResponse {
  secret: string
  uri: string
}

export interface PaginatedResponse<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

export interface ExchangeRateResponse {
  base: string
  rates: Record<string, number>
  updatedAt: string
  warning?: string
}
