import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import SnapshotDetailModal from '../../src/components/snapshots/SnapshotDetailModal.vue'
import type { SnapshotBatch } from '../../src/types'

const snapshot: SnapshotBatch = {
  id: 1,
  name: '美元快照',
  snapshotDate: '2026-08-01T00:00:00Z',
  notes: null,
  totalAssets: 12345,
  totalLiabilities: 0,
  totalNetWorth: 12345,
  netWorthBasis: 'AssetsMinusLiabilities',
  totalBankBalance: 12345,
  totalStockValue: 0,
  totalStockCost: 0,
  bankDetails: [{
    bankName: '美元銀行',
    accountNumber: '12345',
    accountType: '活期',
    currencyCode: 'USD',
    balance: 310,
    exchangeRate: 0.031,
    baseCurrencyCode: 'TWD',
    convertedBalance: 12345,
  }],
  stockDetails: [],
  exchangeRateUpdatedAt: '2026-08-01T00:00:00Z',
  exchangeRateIsStale: false,
}

describe('snapshot detail stored exchange rates', () => {
  it('renders the persisted rate in both desktop and mobile bank details', () => {
    const wrapper = mount(SnapshotDetailModal, {
      props: { open: true, snapshot },
      global: {
        provide: {
          timeZone: {
            timeZoneId: { value: 'Asia/Taipei' },
            isReady: { value: true },
            loadError: { value: false },
            getToday: () => '2026-08-02',
            formatDateTime: (value: string) => value,
          },
        },
        stubs: {
          Modal: {
            template: '<div role="dialog"><slot /></div>',
          },
        },
      },
    })

    expect(wrapper.text()).toContain('保存匯率')
    expect(wrapper.find('table').text()).toContain('1 TWD = 0.031 USD')
    expect(wrapper.find('table').text()).toContain('$12,345.00')
    expect(wrapper.find('article').text()).toContain('1 TWD = 0.031 USD')
    expect(wrapper.find('article').text()).toContain('$12,345.00')
  })
})
