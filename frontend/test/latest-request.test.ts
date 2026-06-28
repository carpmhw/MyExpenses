import assert from 'node:assert/strict'
import { test } from 'node:test'
import { createLatestRequestGuard } from '../src/utils/latestRequest.ts'

// Verifies older request ids are rejected after a newer request starts.
test('createLatestRequestGuard only accepts the newest request id', () => {
  const guard = createLatestRequestGuard()

  const firstRequestId = guard.next()
  const secondRequestId = guard.next()

  assert.equal(guard.isLatest(firstRequestId), false)
  assert.equal(guard.isLatest(secondRequestId), true)
})
