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

export interface CreditCard {
  id: number
  bankName: string
  lastFourDigits: string
  statementDay: number
  dueDay: number
  creditLimit: number
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

export interface Stock {
  id: number
  name: string
  symbol: string
  shares: number
  buyPrice: number
  currentPrice: number
  broker: string | null
  lastPriceUpdate: string | null
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
  stocks: { name: string; symbol: string; shares: number; currentPrice: number; marketValue: number }[]
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

export interface BankDetail {
  bankName: string
  accountNumber: string
  accountType: string
  balance: number
}

export interface StockDetail {
  name: string
  symbol: string
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
  totalNetWorth: number
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

export interface TrendPoint {
  id: number
  date: string
  name: string
  totalNetWorth: number
  totalBankBalance: number
  totalStockValue: number
  totalStockCost: number
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
    totalNetWorth: number
    totalBankBalance: number
    totalStockValue: number
    totalStockCost: number
  }
  snapshot2: {
    id: number
    date: string
    name: string
    totalNetWorth: number
    totalBankBalance: number
    totalStockValue: number
    totalStockCost: number
  }
  differences: {
    netWorth: SnapshotDiff
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
