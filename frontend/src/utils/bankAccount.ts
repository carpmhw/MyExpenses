import type { BankAccount, CurrencyCode } from '../types'

/** 保存銀行帳戶表單需要提交的欄位。 */
export interface BankAccountForm {
  bankName: string
  accountNumber: string
  balance: number
  accountType: string
  currencyCode: CurrencyCode
}

/** 建立新增或編輯銀行帳戶表單，未指定時固定預設為 TWD。 */
export function createBankAccountForm(account?: Partial<Pick<BankAccount, keyof BankAccountForm>>): BankAccountForm {
  return {
    bankName: account?.bankName ?? '',
    accountNumber: account?.accountNumber ?? '',
    balance: account?.balance ?? 0,
    accountType: account?.accountType ?? '',
    currencyCode: account?.currencyCode ?? 'TWD',
  }
}

/** 判斷編輯時幣別是否變更，供 UI 顯示不自動換算餘額的警告。 */
export function hasCurrencyChanged(original: CurrencyCode | null | undefined, next: CurrencyCode): boolean {
  return original !== undefined && original !== null && original !== next
}
