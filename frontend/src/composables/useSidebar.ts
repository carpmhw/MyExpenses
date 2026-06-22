import { ref, onMounted, onUnmounted, watch } from 'vue'
import { useRoute } from 'vue-router'

const isMobile = ref(false)
const isTablet = ref(false)
const isSidebarOpen = ref(false)

const MOBILE_BREAKPOINT = 768
const TABLET_BREAKPOINT = 1024

/**
 * Sidebar composable managing responsive breakpoints and drawer state.
 * Reactive `isMobile` and `isTablet` via matchMedia, `isSidebarOpen` for mobile drawer.
 */
export function useSidebar() {
  const route = useRoute()

  function checkBreakpoints() {
    const w = window.innerWidth
    isMobile.value = w < MOBILE_BREAKPOINT
    isTablet.value = w >= MOBILE_BREAKPOINT && w < TABLET_BREAKPOINT
    if (!isMobile.value) {
      isSidebarOpen.value = false
    }
  }

  function openSidebar() {
    isSidebarOpen.value = true
  }

  function closeSidebar() {
    isSidebarOpen.value = false
  }

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

  return { isMobile, isTablet, isSidebarOpen, openSidebar, closeSidebar }
}
