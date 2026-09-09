import { mount } from '@vue/test-utils'
import { createMemoryHistory, createRouter } from 'vue-router'
import { describe, expect, it, vi } from 'vitest'
import { nextTick, ref } from 'vue'
import MainLayout from '../../src/layouts/MainLayout.vue'
import MobileHeader from '../../src/components/ui/MobileHeader.vue'

const SIDEBAR_COLLAPSED_KEY = 'myexpenses.sidebar.collapsed'

/** 設定整合測試的視窗寬度，模擬實際響應式斷點切換。 */
function setViewportWidth(width: number): void {
  Object.defineProperty(window, 'innerWidth', {
    configurable: true,
    writable: true,
    value: width,
  })
}

/** 建立 MainLayout 測試所需的 router 與主題依賴。 */
async function mountMainLayout() {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: { template: '<div />' } },
      { path: '/dashboard', component: { template: '<div />' } },
      { path: '/:pathMatch(.*)*', component: { template: '<div />' } },
    ],
  })
  await router.push('/')
  await router.isReady()

  const darkMode = { isDark: ref(false), toggle: vi.fn() }
  const wrapper = mount(MainLayout, {
    global: {
      plugins: [router],
      provide: { darkMode },
      stubs: {
        ExchangeRateDialog: { template: '<div />' },
        RouterView: { template: '<div />' },
      },
    },
  })
  await nextTick()
  return { wrapper, router }
}

describe('MainLayout sidebar integration', () => {
  it('persists actual desktop toggle changes and keeps the mobile drawer expanded', async () => {
    localStorage.clear()
    setViewportWidth(1280)
    const { wrapper } = await mountMainLayout()

    expect(wrapper.get('aside').classes()).toContain('w-60')
    const collapse = wrapper.get('button[aria-label="收合側邊欄"]')
    await collapse.trigger('click')
    await nextTick()
    expect(wrapper.get('aside').classes()).toContain('w-16')
    expect(localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe('true')

    setViewportWidth(375)
    window.dispatchEvent(new Event('resize'))
    await nextTick()
    const menu = wrapper.findComponent(MobileHeader).get('button')
    await menu.trigger('click')
    await nextTick()
    expect(wrapper.get('aside').classes()).toContain('w-64')
    expect(wrapper.text()).toContain('MyExpenses')
    expect(wrapper.find('button[aria-label="收合側邊欄"]').exists()).toBe(false)

    await wrapper.get('a[href="/dashboard"]').trigger('click')
    await nextTick()
    expect(wrapper.get('aside').classes()).toContain('hidden')
    expect(localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe('true')

    setViewportWidth(1280)
    window.dispatchEvent(new Event('resize'))
    await nextTick()
    expect(wrapper.get('aside').classes()).toContain('w-16')

    const expand = wrapper.get('button[aria-label="展開側邊欄"]')
    await expand.trigger('click')
    await nextTick()
    expect(wrapper.get('aside').classes()).toContain('w-60')
    expect(localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe('false')
    wrapper.unmount()
  })
})
