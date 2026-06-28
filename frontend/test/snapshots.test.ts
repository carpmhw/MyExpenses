import assert from 'node:assert/strict'
import { test } from 'node:test'
import { buildSnapshotQuery } from '../src/api/index.ts'
import { coerceSnapshotDateRange, createDefaultSnapshotDateRange } from '../src/utils/snapshot.ts'

// Verifies snapshot list/trend query strings include date range filters when provided.
test('buildSnapshotQuery includes date range filters', () => {
  const query = new URLSearchParams(buildSnapshotQuery({
    page: 2,
    pageSize: 15,
    dateStart: '2025-06-27',
    dateEnd: '2026-06-27',
  }))

  assert.equal(query.get('page'), '2')
  assert.equal(query.get('pageSize'), '15')
  assert.equal(query.get('dateStart'), '2025-06-27')
  assert.equal(query.get('dateEnd'), '2026-06-27')
})

// Verifies default snapshot range starts one year before the supplied current date.
test('createDefaultSnapshotDateRange returns the most recent one-year range', () => {
  const range = createDefaultSnapshotDateRange(new Date(2026, 5, 27))

  assert.deepEqual(range, {
    dateStart: '2025-06-27',
    dateEnd: '2026-06-27',
  })
})

// Verifies the default snapshot range clamps leap-day starts to the target month's last valid day.
test('createDefaultSnapshotDateRange clamps leap day to one year earlier', () => {
  const range = createDefaultSnapshotDateRange(new Date(2024, 1, 29))

  assert.deepEqual(range, {
    dateStart: '2023-02-28',
    dateEnd: '2024-02-29',
  })
})

// Verifies blank date filters are omitted from snapshot query strings.
test('buildSnapshotQuery omits blank date range filters', () => {
  const query = new URLSearchParams(buildSnapshotQuery({
    page: 1,
    pageSize: 15,
    dateStart: '   ',
    dateEnd: '',
  }))

  assert.equal(query.get('page'), '1')
  assert.equal(query.get('pageSize'), '15')
  assert.equal(query.has('dateStart'), false)
  assert.equal(query.has('dateEnd'), false)
})

// Verifies invalid ranges are corrected before snapshot requests are sent.
test('coerceSnapshotDateRange corrects end date earlier than start date', () => {
  const result = coerceSnapshotDateRange({
    dateStart: '2026-06-27',
    dateEnd: '2026-06-01',
  })

  assert.deepEqual(result, {
    dateStart: '2026-06-27',
    dateEnd: '2026-06-27',
    changed: true,
    reason: 'end-before-start',
  })
})

// Verifies ranges longer than five years are corrected by preserving the selected end date.
test('coerceSnapshotDateRange limits ranges to five years', () => {
  const result = coerceSnapshotDateRange({
    dateStart: '2020-06-27',
    dateEnd: '2026-06-28',
  })

  assert.deepEqual(result, {
    dateStart: '2021-06-28',
    dateEnd: '2026-06-28',
    changed: true,
    reason: 'range-too-long',
  })
})

// Verifies exactly five calendar years remains valid.
test('coerceSnapshotDateRange accepts exactly five years', () => {
  const result = coerceSnapshotDateRange({
    dateStart: '2021-06-28',
    dateEnd: '2026-06-28',
  })

  assert.deepEqual(result, {
    dateStart: '2021-06-28',
    dateEnd: '2026-06-28',
    changed: false,
    reason: null,
  })
})

// Verifies exactly five calendar years ending on a leap day clamps to the prior valid date.
test('coerceSnapshotDateRange accepts exactly five years ending on leap day', () => {
  const result = coerceSnapshotDateRange({
    dateStart: '2019-02-28',
    dateEnd: '2024-02-29',
  })

  assert.deepEqual(result, {
    dateStart: '2019-02-28',
    dateEnd: '2024-02-29',
    changed: false,
    reason: null,
  })
})
