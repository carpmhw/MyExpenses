import { afterEach, beforeEach, vi } from 'vitest'

// Resets browser globals between component and composable tests.
beforeEach(() => {
  localStorage.clear()
  document.body.innerHTML = ''
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: () => undefined,
    removeListener: () => undefined,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
    dispatchEvent: () => false,
  }))
  vi.stubGlobal('ResizeObserver', class {
    observe() { return undefined }
    unobserve() { return undefined }
    disconnect() { return undefined }
  })
})

// Releases browser stubs and mounted DOM after each isolated test.
afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})
