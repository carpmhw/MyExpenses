import assert from 'node:assert/strict'
import { test } from 'node:test'
import { buildBankAccountsQuery } from '../src/api/index.ts'
import { SUPPORTED_CURRENCY_CODES } from '../src/utils/currency.ts'
import { createBankAccountForm, hasCurrencyChanged } from '../src/utils/bankAccount.ts'
import { formatCurrency } from '../src/utils/format.ts'

// Verifies bank account list queries include trimmed bank name filters.
test('buildBankAccountsQuery includes trimmed bankName filter', () => {
  const query = new URLSearchParams(buildBankAccountsQuery({ page: 2, pageSize: 15, bankName: ' 國泰 ' }))

  assert.equal(query.get('page'), '2')
  assert.equal(query.get('pageSize'), '15')
  assert.equal(query.get('bankName'), '國泰')
})

// Verifies blank bank name filters are omitted so the API returns all accounts.
test('buildBankAccountsQuery omits blank bankName filter', () => {
  const query = new URLSearchParams(buildBankAccountsQuery({ page: 1, pageSize: 15, bankName: '   ' }))

  assert.equal(query.get('page'), '1')
  assert.equal(query.get('pageSize'), '15')
  assert.equal(query.has('bankName'), false)
})

// Verifies new bank-account forms default to the fixed base currency.
test('createBankAccountForm defaults currency to TWD', () => {
  const form = createBankAccountForm()

  assert.equal(form.currencyCode, 'TWD')
})

// Verifies edit forms preserve the account currency instead of guessing a conversion.
test('createBankAccountForm preserves the account currency for edits', () => {
  const form = createBankAccountForm({
    bankName: '美元銀行',
    accountNumber: '12345',
    balance: 310,
    accountType: '活期',
    currencyCode: 'USD',
  })

  assert.equal(form.currencyCode, 'USD')
  assert.equal(form.balance, 310)
  assert.equal(hasCurrencyChanged('USD', form.currencyCode), false)
  assert.equal(hasCurrencyChanged('USD', 'JPY'), true)
})

// Verifies the UI exposes only the backend-supported fixed currency options.
test('supported currency options remain fixed', () => {
  assert.deepEqual(SUPPORTED_CURRENCY_CODES, ['TWD', 'USD', 'JPY', 'CNY', 'HKD'])
})

// Verifies currency formatting keeps every supported code and an explicit unavailable state.
test('formatCurrency formats supported currencies and null as unavailable', () => {
  for (const code of SUPPORTED_CURRENCY_CODES) {
    assert.match(formatCurrency(1234.5, code), /\d/)
  }
  assert.equal(formatCurrency(null, 'TWD'), '不可用')
})
