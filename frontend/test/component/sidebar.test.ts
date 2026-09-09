import { mount } from '@vue/test-utils'
import { createMemoryHistory, createRouter } from 'vue-router'
import { describe, expect, it, vi } from 'vitest'
import { nextTick, ref } from 'vue'
import Sidebar from '../../src/layouts/Sidebar.vue'

/** 建立 Sidebar 測試所需的 router 與主題注入。 */
async function mountSidebar(props: Record<string, unknown> = {}) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: { template: '<div />' } },
      { path: '/dashboard', component: { template: '<div />' } },
      { path: '/settings', component: { template: '<div />' } },
      { path: '/:pathMatch(.*)*', component: { template: '<div />' } },
    ],
  })
  await router.push('/')
  await router.isReady()

  const darkMode = { isDark: ref(false), toggle: vi.fn() }
  const wrapper = mount(Sidebar, {
    props,
    global: {
      plugins: [router],
      provide: { darkMode },
    },
  })
  return { wrapper, router, darkMode }
}

describe('Sidebar responsive presentation', () => {
  it('renders the expanded desktop brand and navigation labels', async () => {
    const { wrapper } = await mountSidebar({ isCollapsed: false })

    expect(wrapper.get('aside').classes()).toContain('w-60')
    expect(wrapper.text()).toContain('MyExpenses')
    expect(wrapper.text()).toContain('主選單')
    expect(wrapper.text()).toContain('儀表板')
    wrapper.unmount()
  })

  it('renders a collapsed desktop rail and emits when the control is activated', async () => {
    const { wrapper } = await mountSidebar({ isCollapsed: true })

    expect(wrapper.get('aside').classes()).toContain('w-16')
    expect(wrapper.text()).not.toContain('MyExpenses')
    const toggle = wrapper.get('button[aria-label="展開側邊欄"]')
    expect(toggle.attributes('title')).toBe('展開側邊欄')
    await toggle.trigger('click')
    expect(wrapper.emitted('toggle-collapse')).toHaveLength(1)
    wrapper.unmount()
  })

  it('keeps tablet compact and does not render the desktop collapse control', async () => {
    const { wrapper } = await mountSidebar({ isTablet: true, isCollapsed: false })

    expect(wrapper.get('aside').classes()).toContain('w-16')
    expect(wrapper.text()).not.toContain('MyExpenses')
    expect(wrapper.find('button[aria-label="收合側邊欄"]').exists()).toBe(false)
    expect(wrapper.find('button[aria-label="展開側邊欄"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('keeps the mobile drawer expanded even when the desktop preference is collapsed', async () => {
    const { wrapper } = await mountSidebar({ isMobile: true, isSidebarOpen: true, isCollapsed: true })

    expect(wrapper.get('aside').classes()).toContain('w-64')
    expect(wrapper.text()).toContain('MyExpenses')
    expect(wrapper.text()).toContain('主選單')
    expect(wrapper.find('button[aria-label="收合側邊欄"]').exists()).toBe(false)
    expect(wrapper.find('button[aria-label="展開側邊欄"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('retains every navigation icon and hides compact-only text and section headings', async () => {
    const labels = [
      '儀表板', '提款紀錄', '交易明細', '信用卡交易', '報表分析', '財務快照', '業務排程',
      '分類管理', '支付方式管理', '使用者設定', '股票', '銀行帳戶', '信用卡',
    ]
    const { wrapper } = await mountSidebar({ isCollapsed: true })

    const links = wrapper.findAll('a')
    expect(links).toHaveLength(labels.length)
    labels.forEach(label => {
      const link = wrapper.get(`a[title="${label}"]`)
      expect(link.attributes('aria-label')).toBe(label)
      expect(link.find('svg').exists()).toBe(true)
    })
    expect(wrapper.text()).not.toContain('主選單')
    expect(wrapper.text()).not.toContain('基本資料')
    expect(wrapper.text()).not.toContain('帳戶')
    expect(wrapper.text()).not.toContain('匯率計算機')
    expect(wrapper.text()).not.toContain('深色模式')
    wrapper.unmount()
  })

  it('removes compact-only navigation titles in expanded mode and preserves active styling', async () => {
    const { wrapper, router } = await mountSidebar({ isCollapsed: false })

    expect(wrapper.findAll('a[title]')).toHaveLength(0)
    await router.push('/dashboard')
    await nextTick()
    expect(wrapper.get('a[href="/dashboard"]').classes()).toContain('bg-bg-sidebar-active')
    wrapper.unmount()
  })

  it('keeps compact footer actions named and functional', async () => {
    const { wrapper, darkMode, router } = await mountSidebar({ isCollapsed: true })

    const theme = wrapper.get('button[title="深色模式"]')
    expect(theme.attributes('aria-label')).toBe('深色模式')
    await theme.trigger('click')
    expect(darkMode.toggle).toHaveBeenCalledTimes(1)

    const calculator = wrapper.get('button[title="匯率計算機"]')
    expect(calculator.attributes('aria-label')).toBe('匯率計算機')
    await calculator.trigger('click')
    expect(wrapper.emitted('open-exchange-rate')).toHaveLength(1)

    const user = wrapper.get('button[title="使用者"]')
    expect(user.attributes('aria-label')).toBe('使用者設定')
    await user.trigger('click')
    await router.isReady()
    await new Promise(resolve => setTimeout(resolve, 0))
    expect(router.currentRoute.value.path).toBe('/settings')
    wrapper.unmount()
  })

  it('keeps mobile backdrop and navigation clicks closing the drawer', async () => {
    const { wrapper } = await mountSidebar({ isMobile: true, isSidebarOpen: true })

    const backdrop = wrapper.find('div.fixed.inset-0')
    expect(backdrop.exists()).toBe(true)
    await backdrop.trigger('click')
    expect(wrapper.emitted('close')).toHaveLength(1)

    await wrapper.get('a[href="/dashboard"]').trigger('click')
    expect(wrapper.emitted('close')).toHaveLength(2)
    wrapper.unmount()
  })
})
