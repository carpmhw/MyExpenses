type QueryKeyRecord = Record<string, unknown>

// Normalizes a query identity so optional blanks and object property ordering are insignificant.
export function normalizeQueryKey(value: unknown): string {
  return serializeQueryValue(value)
}

// Serializes query values while preserving primitive and array ordering semantics.
function serializeQueryValue(value: unknown): string {
  if (value === null || value === undefined) return 'null'
  if (typeof value !== 'object') return JSON.stringify(value)
  if (Array.isArray(value)) {
    return `[${value.map(item => serializeQueryValue(item)).join(',')}]`
  }

  const entries = Object.entries(value as QueryKeyRecord)
    .filter(([, item]) => !isBlankQueryValue(item))
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([key, item]) => `${JSON.stringify(key)}:${serializeQueryValue(item)}`)
  return `{${entries.join(',')}}`
}

// Identifies optional query values that carry no filter meaning.
function isBlankQueryValue(value: unknown): boolean {
  return value === null || value === undefined || (typeof value === 'string' && value.trim() === '')
}
