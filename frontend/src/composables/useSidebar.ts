import { ref, onMounted, onUnmounted, watch } from 'vue'
import { useRoute } from 'vue-router'

const isMobile = ref(false)
const isTablet = ref(false)
const isSidebarOpen = ref(false)
const isSidebarCollapsed = ref(false)

const MOBILE_BREAKPOINT = 768
const TABLET_BREAKPOINT = 1024
const SIDEBAR_COLLAPSED_KEY = 'myexpenses.sidebar.collapsed'
let sidebarPreferenceInitialized = false

/** 首次在瀏覽器 setup 階段讀取桌面側邊欄偏好，避免首次畫面閃動或重複載入。 */
function initializeSidebarPreference(): void {
  if (sidebarPreferenceInitialized || typeof window === 'undefined') return

  sidebarPreferenceInitialized = true
  try {
    const storedValue = window.localStorage.getItem(SIDEBAR_COLLAPSED_KEY)
    if (storedValue === 'true' || storedValue === 'false') {
      isSidebarCollapsed.value = storedValue === 'true'
    }
  } catch {
    // 儲存不可用時維持預設展開，不能阻擋 App Shell 初始化。
  }
}

/** 根據視窗寬度更新響應式斷點，離開手機時同步關閉 Drawer。 */
function checkBreakpoints(): void {
  if (typeof window === 'undefined') return

  const w = window.innerWidth
  isMobile.value = w < MOBILE_BREAKPOINT
  isTablet.value = w >= MOBILE_BREAKPOINT && w < TABLET_BREAKPOINT
  if (!isMobile.value) {
    isSidebarOpen.value = false
  }
}

/** 開啟手機版側邊欄 Drawer。 */
function openSidebar(): void {
  isSidebarOpen.value = true
}

/** 關閉手機版側邊欄 Drawer。 */
function closeSidebar(): void {
  isSidebarOpen.value = false
}

/** 更新桌面側邊欄偏好，並在可用時安全寫入瀏覽器儲存。 */
function setSidebarCollapsed(value: boolean): void {
  isSidebarCollapsed.value = value
  if (typeof window === 'undefined') return

  try {
    window.localStorage.setItem(SIDEBAR_COLLAPSED_KEY, String(value))
  } catch {
    // 儲存寫入失敗時保留記憶體狀態，讓本次切換仍可使用。
  }
}

/** 反轉桌面側邊欄偏好，統一透過 setter 處理持久化。 */
function toggleSidebarCollapsed(): void {
  setSidebarCollapsed(!isSidebarCollapsed.value)
}

/** 建立共享的側邊欄狀態，並註冊斷點與路由變更的生命週期處理。 */
export function useSidebar() {
  initializeSidebarPreference()
  const route = useRoute()

  watch(() => route.path, () => {
    closeSidebar()
  })

  onMounted(() => {
    checkBreakpoints()
    window.addEventListener('resize', checkBreakpoints)
  })

  onUnmounted(() => {
    window.removeEventListener('resize', checkBreakpoints)
  })

  return {
    isMobile,
    isTablet,
    isSidebarOpen,
    isSidebarCollapsed,
    openSidebar,
    closeSidebar,
    toggleSidebarCollapsed,
    setSidebarCollapsed,
  }
}
