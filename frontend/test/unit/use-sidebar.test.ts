import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const SIDEBAR_COLLAPSED_KEY = 'myexpenses.sidebar.collapsed'

const mountedWrappers: Array<{ unmount: () => void }> = []

/** 設定測試所需的視窗寬度，確保斷點初始化可重現。 */
function setViewportWidth(width: number): void {
  Object.defineProperty(window, 'innerWidth', {
    configurable: true,
    writable: true,
    value: width,
  })
}

/** 在全新模組快取中掛載 useSidebar，回傳可觀察的共享狀態。 */
async function mountSidebar(storedValue?: string, width = 1280) {
  vi.resetModules()
  setViewportWidth(width)
  if (storedValue === undefined) {
    localStorage.removeItem(SIDEBAR_COLLAPSED_KEY)
  } else {
    localStorage.setItem(SIDEBAR_COLLAPSED_KEY, storedValue)
  }

  const [{ mount }, { defineComponent, h, nextTick }, { createMemoryHistory, createRouter }, sidebarModule] = await Promise.all([
    import('@vue/test-utils'),
    import('vue'),
    import('vue-router'),
    import('../../src/composables/useSidebar'),
  ])
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: { template: '<div />' } },
      { path: '/next', component: { template: '<div />' } },
    ],
  })
  await router.push('/')
  await router.isReady()

  let sidebar: ReturnType<typeof sidebarModule.useSidebar>
  const Host = defineComponent({
    setup() {
      sidebar = sidebarModule.useSidebar()
      return () => h('div')
    },
  })

  const wrapper = mount(Host, { global: { plugins: [router] } })
  mountedWrappers.push(wrapper)
  await nextTick()
  return { router, sidebar: sidebar!, mount, defineComponent, h, nextTick, sidebarModule }
}

/** 在同一個 composable 模組中掛載第二個消費者，驗證共享偏好只初始化一次。 */
async function mountAdditionalSidebar(environment: Awaited<ReturnType<typeof mountSidebar>>) {
  let sidebar: ReturnType<typeof environment.sidebarModule.useSidebar>
  const Host = environment.defineComponent({
    setup() {
      sidebar = environment.sidebarModule.useSidebar()
      return () => environment.h('div')
    },
  })

  const wrapper = environment.mount(Host, { global: { plugins: [environment.router] } })
  mountedWrappers.push(wrapper)
  await environment.nextTick()
  return sidebar!
}

describe('useSidebar desktop preference', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  afterEach(() => {
    mountedWrappers.splice(0).forEach(wrapper => wrapper.unmount())
  })

  it('defaults to expanded without a saved preference', async () => {
    const { sidebar } = await mountSidebar()

    expect(sidebar.isSidebarCollapsed.value).toBe(false)
  })

  it('restores a saved collapsed preference', async () => {
    const { sidebar } = await mountSidebar('true')

    expect(sidebar.isSidebarCollapsed.value).toBe(true)
  })

  it('restores a saved expanded preference', async () => {
    const { sidebar } = await mountSidebar('false')

    expect(sidebar.isSidebarCollapsed.value).toBe(false)
  })

  it('falls back to expanded for an invalid saved preference', async () => {
    const { sidebar } = await mountSidebar('invalid')

    expect(sidebar.isSidebarCollapsed.value).toBe(false)
  })

  it('toggles the preference in both directions and persists it', async () => {
    const { sidebar } = await mountSidebar()

    sidebar.toggleSidebarCollapsed()
    expect(sidebar.isSidebarCollapsed.value).toBe(true)
    expect(localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe('true')

    sidebar.toggleSidebarCollapsed()
    expect(sidebar.isSidebarCollapsed.value).toBe(false)
    expect(localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe('false')
  })

  it('sets the preference and persists the explicit value', async () => {
    const { sidebar } = await mountSidebar()

    sidebar.setSidebarCollapsed(true)
    expect(sidebar.isSidebarCollapsed.value).toBe(true)
    expect(localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe('true')
  })

  it('keeps the in-memory state usable when storage reads and writes throw', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('storage read failed')
    })
    const { sidebar } = await mountSidebar()

    expect(sidebar.isSidebarCollapsed.value).toBe(false)

    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('storage write failed')
    })
    sidebar.setSidebarCollapsed(true)
    expect(sidebar.isSidebarCollapsed.value).toBe(true)
  })

  it('shares one preference across consumers without reloading storage', async () => {
    const environment = await mountSidebar('true')
    localStorage.setItem(SIDEBAR_COLLAPSED_KEY, 'false')
    const secondSidebar = await mountAdditionalSidebar(environment)

    expect(environment.sidebar.isSidebarCollapsed.value).toBe(true)
    expect(secondSidebar.isSidebarCollapsed.value).toBe(true)

    secondSidebar.setSidebarCollapsed(false)
    expect(environment.sidebar.isSidebarCollapsed.value).toBe(false)
    expect(localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe('false')
  })

  it('removes the resize listener when the composable consumer unmounts', async () => {
    const addEventListener = vi.spyOn(window, 'addEventListener')
    const removeEventListener = vi.spyOn(window, 'removeEventListener')
    await mountSidebar()

    expect(addEventListener).toHaveBeenCalledWith('resize', expect.any(Function))
    mountedWrappers.splice(0).forEach(wrapper => wrapper.unmount())
    expect(removeEventListener).toHaveBeenCalledWith('resize', expect.any(Function))
  })

  it('preserves the desktop preference while moving through tablet and mobile breakpoints', async () => {
    const environment = await mountSidebar('true')
    const { sidebar } = environment

    setViewportWidth(768)
    window.dispatchEvent(new Event('resize'))
    expect(sidebar.isTablet.value).toBe(true)
    expect(sidebar.isMobile.value).toBe(false)
    expect(sidebar.isSidebarCollapsed.value).toBe(true)
    expect(localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe('true')

    setViewportWidth(767)
    window.dispatchEvent(new Event('resize'))
    expect(sidebar.isMobile.value).toBe(true)
    sidebar.openSidebar()
    expect(sidebar.isSidebarOpen.value).toBe(true)

    setViewportWidth(1024)
    window.dispatchEvent(new Event('resize'))
    expect(sidebar.isMobile.value).toBe(false)
    expect(sidebar.isTablet.value).toBe(false)
    expect(sidebar.isSidebarOpen.value).toBe(false)
    expect(sidebar.isSidebarCollapsed.value).toBe(true)
    expect(localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe('true')
  })

  it('keeps a saved preference when initially loaded on a smaller viewport', async () => {
    const environment = await mountSidebar('false', 375)
    const { sidebar } = environment

    expect(sidebar.isMobile.value).toBe(true)
    expect(sidebar.isSidebarCollapsed.value).toBe(false)

    setViewportWidth(1280)
    window.dispatchEvent(new Event('resize'))
    expect(sidebar.isMobile.value).toBe(false)
    expect(sidebar.isSidebarCollapsed.value).toBe(false)
    expect(localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe('false')
  })

  it('closes the mobile drawer on route changes without changing desktop preference', async () => {
    const environment = await mountSidebar('true', 375)
    const { router, sidebar } = environment

    sidebar.openSidebar()
    expect(sidebar.isSidebarOpen.value).toBe(true)
    await router.push('/next')
    await environment.nextTick()

    expect(sidebar.isSidebarOpen.value).toBe(false)
    expect(sidebar.isSidebarCollapsed.value).toBe(true)
    expect(localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe('true')
  })
})
