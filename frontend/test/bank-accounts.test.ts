import assert from 'node:assert/strict'
import { test } from 'node:test'
import { buildBankAccountsQuery } from '../src/api/index.ts'

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
