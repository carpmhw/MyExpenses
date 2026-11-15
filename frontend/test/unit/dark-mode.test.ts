import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { mount } from '@vue/test-utils'
import { useDarkMode } from '../../src/composables/useDarkMode'

/** 可由測試控制的 prefers-color-scheme 查詢；changeTo 會觸發已註冊的 change listener。 */
function stubSystemScheme(initialDark: boolean) {
  const listeners = new Set<() => void>()
  const query = {
    matches: initialDark,
    addEventListener: (_: string, listener: () => void) => listeners.add(listener),
    removeEventListener: (_: string, listener: () => void) => listeners.delete(listener),
  }
  vi.stubGlobal('matchMedia', () => query)
  return {
    changeTo(dark: boolean) {
      query.matches = dark
      listeners.forEach(listener => listener())
    },
  }
}

/** 掛載使用 useDarkMode 的最小元件並回傳其 API。 */
function mountDarkMode() {
  let api!: ReturnType<typeof useDarkMode>
  const wrapper = mount(defineComponent({
    setup() {
      api = useDarkMode()
      return () => h('div')
    },
  }))
  return { wrapper, api }
}

/** 讀取目前 <meta name="color-scheme"> 的內容。 */
function metaContent(): string | null {
  return document.querySelector('meta[name="color-scheme"]')?.getAttribute('content') ?? null
}

describe('useDarkMode', () => {
  beforeEach(() => {
    document.head.innerHTML = '<meta name="color-scheme" content="light dark">'
    document.documentElement.classList.remove('dark')
  })

  afterEach(() => {
    document.head.innerHTML = ''
    document.documentElement.classList.remove('dark')
  })

  /** 首次載入只套用系統偏好，不寫入覆寫，之後仍會跟隨系統切換。 */
  it('follows the system preference without persisting an override', () => {
    const system = stubSystemScheme(false)
    const { wrapper, api } = mountDarkMode()

    expect(localStorage.getItem('darkMode')).toBeNull()
    expect(metaContent()).toBe('light dark')

    system.changeTo(true)
    expect(api.isDark.value).toBe(true)
    expect(document.documentElement.classList.contains('dark')).toBe(true)
    wrapper.unmount()
  })

  /** 切成與系統相反時保存覆寫並同步 meta；再切回與系統相同時移除覆寫。 */
  it('stores only overrides that differ from the system and reverts to system on toggle back', () => {
    stubSystemScheme(false)
    const { wrapper, api } = mountDarkMode()

    api.toggle()
    expect(localStorage.getItem('darkMode')).toBe('true')
    expect(metaContent()).toBe('dark')
    expect(document.documentElement.classList.contains('dark')).toBe(true)

    api.toggle()
    expect(localStorage.getItem('darkMode')).toBeNull()
    expect(metaContent()).toBe('light dark')
    expect(document.documentElement.classList.contains('dark')).toBe(false)
    wrapper.unmount()
  })

  /** 系統偏好之後變成與覆寫相同時，覆寫仍須保留，避免無法固定配色。 */
  it('keeps a stored override when the system later matches it', () => {
    const system = stubSystemScheme(false)
    const { wrapper, api } = mountDarkMode()

    api.toggle()
    system.changeTo(true)
    system.changeTo(false)

    expect(localStorage.getItem('darkMode')).toBe('true')
    expect(api.isDark.value).toBe(true)
    wrapper.unmount()
  })

  /** 其他分頁修改覆寫時同步本分頁配色。 */
  it('syncs overrides changed in another tab', () => {
    stubSystemScheme(false)
    const { wrapper, api } = mountDarkMode()

    localStorage.setItem('darkMode', 'true')
    window.dispatchEvent(new StorageEvent('storage', { key: 'darkMode' }))

    expect(api.isDark.value).toBe(true)
    expect(metaContent()).toBe('dark')
    wrapper.unmount()
  })
})
