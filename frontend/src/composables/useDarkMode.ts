import { ref, onMounted, onUnmounted } from 'vue'

const isDark = ref(false)

function applyClass() {
  if (isDark.value) {
    document.documentElement.classList.add('dark')
  } else {
    document.documentElement.classList.remove('dark')
  }
  localStorage.setItem('darkMode', String(isDark.value))
}

/**
 * Dark mode composable with localStorage persistence and system preference detection.
 * Provides reactive `isDark` state and `toggle()` method.
 */
export function useDarkMode() {
  let mediaQuery: MediaQueryList | null = null
  let mediaHandler: ((e: MediaQueryListEvent) => void) | null = null

  function setFromStorage() {
    const stored = localStorage.getItem('darkMode')
    if (stored !== null) {
      isDark.value = stored === 'true'
    } else if (mediaQuery) {
      isDark.value = mediaQuery.matches
    }
    applyClass()
  }

  function toggle() {
    isDark.value = !isDark.value
    applyClass()
  }

  onMounted(() => {
    mediaQuery = window.matchMedia('(prefers-color-scheme: dark)')
    mediaHandler = (e: MediaQueryListEvent) => {
      if (localStorage.getItem('darkMode') === null) {
        isDark.value = e.matches
        applyClass()
      }
    }
    mediaQuery.addEventListener('change', mediaHandler)
    setFromStorage()
  })

  onUnmounted(() => {
    if (mediaQuery && mediaHandler) {
      mediaQuery.removeEventListener('change', mediaHandler)
    }
  })

  return { isDark, toggle }
}
