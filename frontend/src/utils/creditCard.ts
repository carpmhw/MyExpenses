export const CARD_NETWORK_OPTIONS = [
  { value: 'VISA', label: 'VISA' },
  { value: 'Mastercard', label: 'Mastercard' },
  { value: 'JCB', label: 'JCB' },
  { value: 'American Express', label: 'American Express' },
  { value: 'UnionPay', label: 'UnionPay' },
  { value: '其他', label: '其他' },
]

// Formats optional credit card metadata for table cells.
export function formatOptionalCreditCardText(value: string | null | undefined): string {
  const trimmed = value?.trim() ?? ''
  return trimmed || '-'
}

// Normalizes optional credit card form fields before sending API payloads.
export function normalizeOptionalCreditCardField(value: string | null | undefined): string | null {
  const trimmed = value?.trim() ?? ''
  return trimmed || null
}
