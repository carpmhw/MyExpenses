import assert from 'node:assert/strict'
import test from 'node:test'
import {
  addCalendarYears,
  formatDateOnly,
  formatSystemDateTime,
  getDateInputValue,
  getSystemDateParts,
  isDateOnlyBefore,
} from '../src/utils/timezone.ts'

// Verifies that a UTC instant is converted to the configured system calendar date.
test('getDateInputValue uses the supplied time zone across UTC rollover', () => {
  const instant = new Date('2026-07-14T23:30:00.000Z')

  assert.equal(getDateInputValue(instant, 'Asia/Taipei'), '2026-07-15')
  assert.equal(getDateInputValue(instant, 'America/Los_Angeles'), '2026-07-14')
})

// Verifies a form opened near midnight follows the configured system date instead of UTC.
test('date defaults follow the system date when browser-local and UTC dates differ', () => {
  const instant = new Date('2026-07-15T00:30:00.000Z')

  assert.equal(getDateInputValue(instant, 'America/Los_Angeles'), '2026-07-14')
  assert.equal(getDateInputValue(instant, 'Asia/Taipei'), '2026-07-15')
})

// Verifies that calendar dates are formatted without parsing them as UTC instants.
test('formatDateOnly preserves the date-only value', () => {
  assert.equal(formatDateOnly('2026-07-15'), '2026/07/15')
  assert.equal(formatDateOnly('2026-07-15', '-'), '2026-07-15')
})

// Verifies date-only comparisons do not reinterpret a calendar date as a UTC instant.
test('isDateOnlyBefore compares calendar dates as strings', () => {
  assert.equal(isDateOnlyBefore('2026-07-14', '2026-07-15'), true)
  assert.equal(isDateOnlyBefore('2026-07-15', '2026-07-15'), false)
})

// Verifies that date-only arithmetic clamps leap-day calculations to valid dates.
test('addCalendarYears clamps leap days', () => {
  assert.equal(addCalendarYears('2024-02-29', -1), '2023-02-28')
  assert.equal(addCalendarYears('2024-02-29', 1), '2025-02-28')
})

// Verifies that system date parts are derived from a named time zone.
test('getSystemDateParts returns local year and month', () => {
  const parts = getSystemDateParts(new Date('2026-12-31T23:30:00.000Z'), 'Asia/Taipei')

  assert.deepEqual(parts, { year: 2027, month: 1, day: 1 })
})

// Verifies that event timestamps are presented in the configured system zone.
test('formatSystemDateTime displays an event in the configured zone', () => {
  assert.equal(
    formatSystemDateTime('2026-07-14T23:30:00.000Z', 'Asia/Taipei'),
    '2026/07/15 07:30',
  )
})
