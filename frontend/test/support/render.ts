import { mount, type MountingOptions } from '@vue/test-utils'
import { createMemoryHistory, createRouter, type RouteRecordRaw } from 'vue-router'
import type { Component } from 'vue'
import { ref } from 'vue'

export interface TestAuthState {
  token: ReturnType<typeof ref<string | null>>
  user: ReturnType<typeof ref<unknown | null>>
  isAuthenticated: ReturnType<typeof ref<boolean>>
}

// Creates the common authentication provider shape expected by application components.
export function createTestAuth(authenticated = true): TestAuthState {
  return {
    token: ref(authenticated ? 'test-token' : null),
    user: ref(authenticated ? { id: 1 } : null),
    isAuthenticated: ref(authenticated),
  }
}

// Creates a memory router suitable for rendered page tests.
export function createTestRouter(routes: RouteRecordRaw[] = []): ReturnType<typeof createRouter> {
  return createRouter({
    history: createMemoryHistory(),
    routes: routes.length > 0 ? routes : [{ path: '/', component: { template: '<div />' } }],
  })
}

// Mounts a component with the app-level providers used by core pages.
export function mountWithAppProviders<T extends Component>(
  component: T,
  options: MountingOptions<T> = {},
) {
  const router = createTestRouter()
  const auth = createTestAuth()
  const timeZone = {
    timeZoneId: ref('Asia/Taipei'),
    isReady: ref(true),
    loadError: ref(false),
    getToday: () => '2026-08-02',
    formatDateTime: (value: string | Date) => typeof value === 'string' ? value : value.toISOString(),
  }
  const toast = { success: () => undefined, error: () => undefined }
  const darkMode = { isDark: ref(false), toggle: () => undefined }

  return mount(component, {
    ...options,
    global: {
      ...options.global,
      plugins: [...(options.global?.plugins ?? []), router],
      provide: {
        ...options.global?.provide,
        auth,
        toast,
        timeZone,
        darkMode,
      },
    },
  })
}
