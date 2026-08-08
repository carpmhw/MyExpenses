import { describe, expect, it } from 'vitest'
import { api, buildScheduleExecutionsQuery } from '../../src/api'
import { createFetchMock, jsonResponse } from '../support/deferred'

describe('schedule API client', () => {
  it('omits blank filters and serializes date and pagination filters', () => {
    expect(buildScheduleExecutionsQuery({
      jobKey: '  ',
      status: 'Failed',
      dateStart: '2026-08-01',
      dateEnd: '2026-08-08',
      page: 2,
      pageSize: 50,
    })).toBe('status=Failed&dateStart=2026-08-01&dateEnd=2026-08-08&page=2&pageSize=50')
  })

  it('uses the central request client and forwards AbortSignal', async () => {
    const fetchMock = createFetchMock(() => jsonResponse([]))
    const controller = new AbortController()

    await api.schedules.overview({ signal: controller.signal })

    expect(fetchMock).toHaveBeenCalledWith(
      '/api/schedules',
      expect.objectContaining({ signal: controller.signal }),
    )
  })
})
