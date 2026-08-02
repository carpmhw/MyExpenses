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
