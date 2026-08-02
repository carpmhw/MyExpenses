type JsonValue = null | boolean | number | string | JsonValue[] | { [key: string]: JsonValue }

interface IdempotencyKeyState {
  prepare(payload: unknown): string
  clear(): void
}

// Creates stable JSON text so object property order does not change a command fingerprint.
function stableSerialize(value: unknown): string {
  if (value === null || typeof value !== 'object') return JSON.stringify(value)
  if (Array.isArray(value)) return `[${value.map(item => stableSerialize(item)).join(',')}]`

  const entries = Object.entries(value as Record<string, unknown>)
    .filter(([, item]) => item !== undefined)
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([key, item]) => `${JSON.stringify(key)}:${stableSerialize(item)}`)
  return `{${entries.join(',')}}`
}

// Generates a UUID for a logical financial submission in browser and test environments.
function createUuid(): string {
  if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID()
  const bytes = new Uint8Array(16)
  globalThis.crypto?.getRandomValues?.(bytes)
  bytes[6] = (bytes[6] & 0x0f) | 0x40
  bytes[8] = (bytes[8] & 0x3f) | 0x80
  const hex = [...bytes].map(byte => byte.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

// Keeps one idempotency key while a logical form payload is retried unchanged.
export function createIdempotencyKeyState(): IdempotencyKeyState {
  let key: string | null = null
  let fingerprint: string | null = null

  return {
    prepare(payload: unknown) {
      const nextFingerprint = stableSerialize(payload)
      if (key === null || fingerprint !== nextFingerprint) {
        key = createUuid()
        fingerprint = nextFingerprint
      }
      return key
    },
    clear() {
      key = null
      fingerprint = null
    },
  }
}

export type { JsonValue }
