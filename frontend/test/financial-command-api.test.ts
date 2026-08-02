import assert from 'node:assert/strict'
import { test } from 'node:test'
import { api } from '../src/api/index.ts'

const requestLog: Array<{ url: string; init?: RequestInit }> = []

function installFetchStub() {
  requestLog.length = 0
  ;(globalThis as typeof globalThis & { localStorage: Storage }).localStorage = {
    getItem: () => 'test-token',
    setItem: () => undefined,
    removeItem: () => undefined,
    clear: () => undefined,
    key: () => null,
    length: 0,
  }
  ;(globalThis as typeof globalThis & { fetch: typeof fetch }).fetch = async (input, init) => {
    requestLog.push({ url: String(input), init })
    return new Response(JSON.stringify({ id: 1, payments: [], remainingPeriods: 3, status: 'Active' }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }
}

// Verifies composite installment purchases use one endpoint and an idempotency header.
test('installment purchase API sends one composite idempotent request', async () => {
  installFetchStub()

  await api.installmentPurchases.create({
    transaction: {
      type: 'Expense',
      amount: 1200,
      date: '2026-06-20',
      description: '測試分期',
      categoryId: 1,
      paymentMethodId: 2,
    },
    installment: { cardId: 3, periods: 3 },
  }, 'key-1')

  assert.equal(requestLog.length, 1)
  assert.equal(requestLog[0].url, '/api/installment-purchases')
  assert.equal(new Headers(requestLog[0].init?.headers).get('Idempotency-Key'), 'key-1')
})

// Verifies standalone installment creation carries the required idempotency key.
test('standalone installment API sends an idempotent canonical command', async () => {
  installFetchStub()

  await api.installments.create({
    transactionId: null,
    cardId: 3,
    totalAmount: 1200,
    periods: 3,
    purchaseDate: '2026-06-20',
    description: '測試分期',
  }, 'key-2')

  assert.equal(requestLog.length, 1)
  assert.equal(requestLog[0].url, '/api/installments')
  assert.equal(new Headers(requestLog[0].init?.headers).get('Idempotency-Key'), 'key-2')
  assert.deepEqual(JSON.parse(String(requestLog[0].init?.body)), {
    transactionId: null,
    cardId: 3,
    totalAmount: 1200,
    periods: 3,
    purchaseDate: '2026-06-20',
    description: '測試分期',
  })
})

// Verifies payment updates send the desired state instead of relying on toggling.
test('installment payment API sends explicit target state', async () => {
  installFetchStub()

  await api.installments.markPayment(7, 11, { isPaid: true, paidDate: '2026-06-20' })

  const body = JSON.parse(String(requestLog[0].init?.body)) as { isPaid: boolean; paidDate: string }
  assert.equal(requestLog[0].url, '/api/installments/7/payments/11')
  assert.equal(body.isPaid, true)
  assert.equal(body.paidDate, '2026-06-20')
})
