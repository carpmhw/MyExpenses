import { ref } from 'vue'
import type { User } from '../types'

const token = ref<string | null>(localStorage.getItem('authToken'))
const user = ref<User | null>(null)
const isAuthenticated = ref(!!token.value)

export function useAuth() {
  function setAuth(newToken: string, newUser: User) {
    token.value = newToken
    user.value = newUser
    isAuthenticated.value = true
    localStorage.setItem('authToken', newToken)
  }

  async function logout() {
    try {
      await fetch('/api/auth/logout', { method: 'POST' })
    } catch {
      // 即使後端呼叫失敗，前端仍清除登入狀態
    }
    token.value = null
    user.value = null
    isAuthenticated.value = false
    localStorage.removeItem('authToken')
  }

  async function fetchStatus() {
    try {
      const res = await fetch('/api/auth/status', {
        headers: token.value ? { 'Authorization': `Bearer ${token.value}` } : {},
      })
      if (!res.ok) {
        logout()
        return
      }
      const data = await res.json()
      if (data.authenticated && data.user) {
        user.value = data.user
        isAuthenticated.value = true
      } else {
        logout()
      }
    } catch {
      // 網路錯誤時不清除 token，保留現有登入狀態
    }
  }

  return {
    token,
    user,
    isAuthenticated,
    setAuth,
    logout,
    fetchStatus,
  }
}
