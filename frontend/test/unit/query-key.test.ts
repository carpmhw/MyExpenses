import { describe, expect, it } from 'vitest'
import { normalizeQueryKey } from '../../src/utils/queryKey'

describe('normalizeQueryKey', () => {
  it('treats blank optional values and object ordering as equivalent', () => {
    expect(normalizeQueryKey({ page: 1, search: '', categoryId: undefined }))
      .toBe(normalizeQueryKey({ categoryId: '', page: 1 }))
  })

  it('preserves primitive array values and their order', () => {
    expect(normalizeQueryKey(['transactions', 0, false, ['a', 'b']]))
      .not.toBe(normalizeQueryKey(['transactions', false, 0, ['a', 'b']]))
  })

  it('changes identity for period, filter, page, tab, and selected resource changes', () => {
    const base = ['reports', '2026-08', 'summary', 1]
    expect(normalizeQueryKey(base)).not.toBe(normalizeQueryKey(['reports', '2026-09', 'summary', 1]))
    expect(normalizeQueryKey(base)).not.toBe(normalizeQueryKey(['reports', '2026-08', 'trend', 1]))
    expect(normalizeQueryKey(base)).not.toBe(normalizeQueryKey(['reports', '2026-08', 'summary', 2]))
  })
})
