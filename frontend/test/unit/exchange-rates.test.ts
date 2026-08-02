import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../src/api'
import { useExchangeRates } from '../../src/composables/useExchangeRates'

describe('useExchangeRates', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads rates through the central API client with an owned signal', async () => {
    const getRates = vi.spyOn(api.exchangeRates, 'get').mockResolvedValue({
      base: 'TWD',
      rates: { TWD: 1, USD: 0.03 },
      updatedAt: '2026-08-02T10:00:00Z',
    })
    const rates = useExchangeRates()

    await rates.fetchRates()

    expect(getRates).toHaveBeenCalledWith(expect.objectContaining({ signal: expect.any(AbortSignal) }))
    expect(rates.rates.value.USD).toBe(0.03)
    expect(rates.error.value).toBeNull()
  })
})
