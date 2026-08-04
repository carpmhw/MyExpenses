import assert from 'node:assert/strict'
import { test } from 'node:test'
import { createIdempotencyKeyState } from '../src/utils/idempotency.ts'

// Verifies unchanged financial form payloads reuse one retry key.
test('idempotency key state reuses key for an unchanged payload', () => {
  const state = createIdempotencyKeyState()
  const first = state.prepare({ amount: 100, description: '早餐' })
  const second = state.prepare({ description: '早餐', amount: 100 })

  assert.equal(first, second)
})

// Verifies changing a logical command payload creates a new retry key.
test('idempotency key state replaces key when payload changes', () => {
  const state = createIdempotencyKeyState()
  const first = state.prepare({ amount: 100 })
  const second = state.prepare({ amount: 101 })

  assert.notEqual(first, second)
})

// Verifies a confirmed command clears its key before the next submission.
test('idempotency key state clears completed submissions', () => {
  const state = createIdempotencyKeyState()
  const first = state.prepare({ amount: 100 })
  state.clear()

  assert.notEqual(first, state.prepare({ amount: 100 }))
})

// Verifies a new form submission receives a new key even when its payload matches an older form.
test('idempotency key state starts a new logical form submission', () => {
  const state = createIdempotencyKeyState({ createKey: () => `key-${++keyCounter}` })
  const first = state.prepare({ amount: 100 })
  state.begin()

  assert.notEqual(first, state.prepare({ amount: 100 }))
})

// Verifies canonical payload fingerprints ignore transport-only null and whitespace differences.
test('idempotency key state canonicalizes supported financial payloads', () => {
  const state = createIdempotencyKeyState({ createKey: () => `key-${++keyCounter}` })
  const first = state.prepare({ description: ' 早餐 ', notes: null, perPeriod: 50 })
  const second = state.prepare({ description: '早餐', notes: undefined, perPeriod: 99 })

  assert.equal(first, second)
})

let keyCounter = 0
