import { describe, expect, it } from 'vitest'
import { api } from '../../src/api'
import { createFetchMock, jsonResponse } from '../support/deferred'

describe('固定預設 API', () => {
  // 驗證共用 client 的路由、明確 PUT 目標與取消訊號，避免偏好寫入走錯資源。
  it('reads nullable defaults and sets each resource independently with the owner request context', async () => {
    const fetchMock = createFetchMock(() => jsonResponse({ defaultBankAccountId: 8, defaultCreditCardId: null }))
    const controller = new AbortController()
    const context = { signal: controller.signal }
    expect(await api.settings.getTransactionDefaults(context)).toEqual({ defaultBankAccountId: 8, defaultCreditCardId: null })
    await api.bankAccounts.setDefault(8, context)
    await api.creditCards.setDefault(9, context)
    expect(fetchMock.mock.calls.map(call => [call[0], call[1]?.method ?? 'GET'])).toEqual([
      ['/api/settings/transaction-defaults', 'GET'],
      ['/api/bank-accounts/8/default', 'PUT'],
      ['/api/credit-cards/9/default', 'PUT'],
    ])
    for (const [, options] of fetchMock.mock.calls) {
      expect(options?.signal).toBe(controller.signal)
      expect(options?.body).toBeUndefined()
    }
  })
})
