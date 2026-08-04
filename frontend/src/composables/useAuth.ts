import { computed, ref } from 'vue'
import type { User } from '../types'
import { api, configureApiSession } from '../api'

export type AuthState = 'unknown' | 'authenticated' | 'guest'

interface AuthStatusResult {
  authenticated: boolean
  user: User | null
  hasUsers: boolean
}

const initialToken = typeof localStorage === 'undefined' ? null : localStorage.getItem('authToken')
const token = ref<string | null>(initialToken)
const user = ref<User | null>(null)
const authState = ref<AuthState>('unknown')
const hasUsers = ref<boolean | null>(null)
const isAuthenticated = computed(() => authState.value === 'authenticated')
let operationGeneration = 0
let initializationPromise: Promise<AuthStatusResult> | null = null

// Clears all local session state before any best-effort server notification.
function clearLocalSession(): void {
  token.value = null
  user.value = null
  authState.value = 'guest'
  if (typeof localStorage !== 'undefined') localStorage.removeItem('authToken')
}

// Expires only the session represented by the token that initiated the failing request.
function expireSession(tokenSnapshot: string): void {
  if (token.value !== tokenSnapshot) return
  operationGeneration++
  clearLocalSession()
}

// Stores a newly authenticated session and invalidates older bootstrap operations.
function setAuth(newToken: string, newUser: User): void {
  operationGeneration++
  token.value = newToken
  user.value = newUser
  authState.value = 'authenticated'
  hasUsers.value = true
  if (typeof localStorage !== 'undefined') localStorage.setItem('authToken', newToken)
}

// Initializes authentication once and shares the same status request with every consumer.
async function initialize(): Promise<AuthStatusResult> {
  if (initializationPromise) return initializationPromise

  const generation = operationGeneration
  const tokenSnapshot = token.value
  let pending: Promise<AuthStatusResult>
  pending = api.auth.status()
    .then(result => {
      if (generation !== operationGeneration || token.value !== tokenSnapshot) return result
      hasUsers.value = result.hasUsers
      if (result.authenticated && result.user) {
        user.value = result.user
        authState.value = 'authenticated'
      } else {
        clearLocalSession()
      }
      return result
  })
    .catch(() => {
      if (generation === operationGeneration) {
        clearLocalSession()
      }
      return {
        authenticated: false,
        user: null,
        hasUsers: hasUsers.value ?? false,
      }
    })
    .finally(() => {
      if (initializationPromise === pending) initializationPromise = null
    })
  initializationPromise = pending
  return pending
}

// Clears the local session synchronously and notifies the server without blocking protection.
async function logout(): Promise<void> {
  operationGeneration++
  clearLocalSession()
  try {
    await api.auth.logout()
  } catch {
    // 本地登出已完成，伺服器通知失敗不應阻擋受保護畫面關閉。
  }
}

// Configures central API 401 handling after the auth state functions exist.
configureApiSession({
  getToken: () => token.value,
  onSessionExpired: expireSession,
})

export function useAuth() {
  return {
    token,
    user,
    authState,
    hasUsers,
    isAuthenticated,
    setAuth,
    logout,
    initialize,
    fetchStatus: initialize,
  }
}
