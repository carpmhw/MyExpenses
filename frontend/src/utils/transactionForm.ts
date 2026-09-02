import type {
  Category,
  PaymentMethod,
  Transaction,
} from '../types'

export type TransactionType = 'Income' | 'Expense'

export interface TransactionFormValues {
  type: TransactionType
  amount: number
  date: string
  categoryId: number | null
  description: string
  notes: string
  paymentMethodId: number | null
}

export interface TransactionFormOptions {
  categories: Category[]
  paymentMethods: PaymentMethod[]
  editing?: Transaction | null
}

export type TransactionData = Omit<Transaction, 'id' | 'createdAt' | 'category' | 'paymentMethod'>
export type TransactionUpdateData = Partial<Pick<Transaction, 'type' | 'amount' | 'date' | 'description' | 'notes' | 'categoryId' | 'paymentMethodId'>>

export type TransactionFormCommand =
  | { kind: 'create'; data: TransactionData }
  | { kind: 'update'; id: number; data: TransactionUpdateData }

export type TransactionFormErrors = Record<string, string>

export interface TransactionCommandResult {
  values: TransactionFormValues
  errors: TransactionFormErrors
  command: TransactionFormCommand | null
}

// 判斷支付方式是否代表信用卡。
export function isCreditCardPaymentMethod(paymentMethod: PaymentMethod | undefined): boolean {
  return paymentMethod?.systemCode === 'credit-card'
}

// 依交易類型建立日期優先的新交易表單初始值。
export function createInitialTransactionForm(
  today: string,
  categories: Category[],
  type: TransactionType = 'Expense',
): TransactionFormValues {
  return {
    type,
    amount: 0,
    date: today,
    categoryId: categories.find(category => category.type === type)?.id ?? null,
    description: '',
    notes: '',
    paymentMethodId: null,
  }
}

// 將既有交易轉成普通交易編輯表單值。
export function createTransactionFormFromItem(item: Transaction): TransactionFormValues {
  return {
    type: item.type,
    amount: item.amount,
    date: item.date.slice(0, 10),
    categoryId: item.categoryId,
    description: item.description || '',
    notes: item.notes || '',
    paymentMethodId: item.paymentMethodId,
  }
}

// 複製表單值，避免元件內部狀態直接修改父層資料。
export function cloneTransactionForm(values: TransactionFormValues): TransactionFormValues {
  return { ...values }
}

// 判斷目前編輯的交易是否需要保留歷史信用卡支付方式。
function isHistoricalCreditCardPayment(
  values: TransactionFormValues,
  options: TransactionFormOptions,
): boolean {
  if (!options.editing || options.editing.paymentMethodId !== values.paymentMethodId) return false
  const paymentMethod = options.paymentMethods.find(item => item.id === values.paymentMethodId)
    ?? options.editing.paymentMethod ?? undefined
  return isCreditCardPaymentMethod(paymentMethod)
}

// 清理交易類型與支付方式切換後不再相容的普通交易狀態。
export function normalizeTransactionForm(
  values: TransactionFormValues,
  options: TransactionFormOptions,
): TransactionFormValues {
  const next = cloneTransactionForm(values)
  const category = options.categories.find(item => item.id === next.categoryId)
  const paymentMethod = options.paymentMethods.find(item => item.id === next.paymentMethodId)
  const preserveHistoricalCreditCard = isHistoricalCreditCardPayment(next, options)

  if (!category || category.type !== next.type) next.categoryId = null

  if (isCreditCardPaymentMethod(paymentMethod) && !preserveHistoricalCreditCard)
    next.paymentMethodId = null

  return next
}

// 驗證目前表單狀態並回傳可供畫面顯示的錯誤集合。
export function validateTransactionForm(
  values: TransactionFormValues,
  options: TransactionFormOptions,
): TransactionFormErrors {
  const normalized = normalizeTransactionForm(values, options)
  const errors: TransactionFormErrors = {}
  const category = options.categories.find(item => item.id === normalized.categoryId)
  const paymentMethod = options.paymentMethods.find(item => item.id === normalized.paymentMethodId)
  const historicalPaymentMethod = options.editing?.paymentMethodId === normalized.paymentMethodId
    ? options.editing.paymentMethod
    : undefined

  if (!normalized.date) errors.date = '請選擇日期'
  if (!Number.isFinite(normalized.amount) || normalized.amount <= 0) errors.amount = '金額必須大於零'
  if (!category || category.type !== normalized.type) errors.categoryId = '請選擇相符的類別'
  if (!normalized.description.trim()) errors.description = '請填寫項目名稱'
  if (normalized.paymentMethodId !== null && !paymentMethod && !historicalPaymentMethod)
    errors.paymentMethodId = '支付方式無效，請重新選擇'

  return errors
}

// 從通過驗證的表單狀態建立唯一且明確的 API 命令。
export function buildTransactionCommand(
  values: TransactionFormValues,
  options: TransactionFormOptions,
): TransactionCommandResult {
  const normalized = normalizeTransactionForm(values, options)
  const errors = validateTransactionForm(normalized, options)
  if (Object.keys(errors).length > 0) return { values: normalized, errors, command: null }

  const transaction: TransactionData = {
    type: normalized.type,
    amount: normalized.amount,
    date: normalized.date,
    categoryId: normalized.categoryId!,
    description: normalized.description.trim(),
    notes: normalized.notes.trim() || null,
    paymentMethodId: normalized.paymentMethodId,
  }

  if (options.editing) {
    return { values: normalized, errors: {}, command: { kind: 'update', id: options.editing.id, data: transaction } }
  }

  return { values: normalized, errors: {}, command: { kind: 'create', data: transaction } }
}

// 依目前有效命令產生一致的送出按鈕文字。
export function getTransactionActionLabel(values: TransactionFormValues, editing: boolean): string {
  if (editing) return '儲存變更'
  return values.type === 'Income' ? '建立收入' : '建立支出'
}
