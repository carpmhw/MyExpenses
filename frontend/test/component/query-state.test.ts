import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import QueryState from '../../src/components/ui/QueryState.vue'

describe('QueryState', () => {
  it('renders loading status with a live region', () => {
    const wrapper = mount(QueryState, { props: { status: 'loading' } })

    expect(wrapper.get('[role="status"]').text()).toContain('載入中')
  })

  it('renders an inline retry action for initial errors', async () => {
    const retry = vi.fn()
    const wrapper = mount(QueryState, { props: { status: 'error', retry } })

    expect(wrapper.get('[role="alert"]').text()).toContain('載入失敗')
    await wrapper.get('button').trigger('click')
    expect(retry).toHaveBeenCalledTimes(1)
  })

  it('keeps stale content visible with a non-blocking warning', () => {
    const wrapper = mount(QueryState, {
      props: { status: 'stale', lastSuccessAt: 0 },
      slots: { default: '<div class="content">資料內容</div>' },
    })

    expect(wrapper.get('.content').text()).toBe('資料內容')
    expect(wrapper.get('[role="status"]').text()).toContain('資料可能已過期')
  })

  it('renders empty state without using the error role', () => {
    const wrapper = mount(QueryState, {
      props: { status: 'empty', emptyMessage: '沒有資料' },
    })

    expect(wrapper.text()).toContain('沒有資料')
    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
  })
})
