import assert from 'node:assert/strict'
import { test } from 'node:test'
import {
  CARD_NETWORK_OPTIONS,
  formatOptionalCreditCardText,
  normalizeOptionalCreditCardField,
} from '../src/utils/creditCard.ts'

// Verifies the card network dropdown uses the supported fixed values from the spec.
test('CARD_NETWORK_OPTIONS lists supported card networks', () => {
  assert.deepEqual(CARD_NETWORK_OPTIONS.map((option) => option.value), [
    'VISA',
    'Mastercard',
    'JCB',
    'American Express',
    'UnionPay',
    '其他',
  ])
})

// Verifies empty optional credit card metadata renders consistently in list cells.
test('formatOptionalCreditCardText returns dash for empty values', () => {
  assert.equal(formatOptionalCreditCardText(null), '-')
  assert.equal(formatOptionalCreditCardText(''), '-')
  assert.equal(formatOptionalCreditCardText('  '), '-')
  assert.equal(formatOptionalCreditCardText('VISA'), 'VISA')
})

// Verifies cleared form fields are sent to the API as null rather than empty strings.
test('normalizeOptionalCreditCardField trims values and converts blanks to null', () => {
  assert.equal(normalizeOptionalCreditCardField(null), null)
  assert.equal(normalizeOptionalCreditCardField(''), null)
  assert.equal(normalizeOptionalCreditCardField('  '), null)
  assert.equal(normalizeOptionalCreditCardField('  主力卡  '), '主力卡')
})
