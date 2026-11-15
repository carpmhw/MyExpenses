import { ref, onMounted, onUnmounted } from 'vue'

/** localStorage 鍵；'true'／'false' 代表使用者覆寫，缺少代表跟隨系統。與 index.html 的防閃爍 script 共用。 */
const STORAGE_KEY = 'darkMode'

const isDark = ref(false)

/** 讀取使用者的深色模式覆寫；回傳 null 代表跟隨系統偏好。 */
function readOverride(): boolean | null {
  const stored = localStorage.getItem(STORAGE_KEY)
  return stored === null ? null : stored === 'true'
}

/**
 * 依覆寫與系統偏好套用目前配色：切換根元素 .dark class（Tailwind 色票依此切換），
 * 並同步 <meta name="color-scheme">，讓原生 UI 與尚未載入 CSS 的畫布一致。不寫入儲存。
 */
function applyScheme(override: boolean | null, systemDark: boolean) {
  isDark.value = override ?? systemDark
  document.documentElement.classList.toggle('dark', isDark.value)
  document.querySelector('meta[name="color-scheme"]')?.setAttribute(
    'content',
    override === null ? 'light dark' : override ? 'dark' : 'light',
  )
}

/**
 * 深色模式 composable：預設跟隨系統偏好，toggle 時只在與系統不同時保存覆寫，
 * 切回與系統相同的配色則移除覆寫，恢復跟隨系統；並同步其他分頁的變更。
 * 提供 reactive `isDark` 與 `toggle()`。
 */
export function useDarkMode() {
  let mediaQuery: MediaQueryList | null = null

  /** 系統偏好改變時，若沒有覆寫就跟著切換；已保存的覆寫一律保留。 */
  function handleSystemChange() {
    if (mediaQuery) applyScheme(readOverride(), mediaQuery.matches)
  }

  /** 其他分頁修改覆寫（或清空儲存）時同步本分頁配色。 */
  function handleStorage(event: StorageEvent) {
    if (event.key === STORAGE_KEY || event.key === null) handleSystemChange()
  }

  /** 切換成目前畫面配色的相反；目標與系統偏好相同時移除覆寫，否則保存覆寫。 */
  function toggle() {
    const systemDark = mediaQuery?.matches ?? false
    const target = !isDark.value
    if (target === systemDark) {
      localStorage.removeItem(STORAGE_KEY)
    } else {
      localStorage.setItem(STORAGE_KEY, String(target))
    }
    applyScheme(readOverride(), systemDark)
  }

  onMounted(() => {
    mediaQuery = window.matchMedia('(prefers-color-scheme: dark)')
    mediaQuery.addEventListener('change', handleSystemChange)
    window.addEventListener('storage', handleStorage)
    handleSystemChange()
  })

  onUnmounted(() => {
    mediaQuery?.removeEventListener('change', handleSystemChange)
    window.removeEventListener('storage', handleStorage)
  })

  return { isDark, toggle }
}
