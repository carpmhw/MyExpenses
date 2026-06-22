<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '../../api'
import { useAuth } from '../../composables/useAuth'

const router = useRouter()
const auth = useAuth()

type Mode = 'loading' | 'register' | 'login' | 'verify-2fa'

const mode = ref<Mode>('loading')
const error = ref('')
const tempToken = ref('')

const email = ref('')
const displayName = ref('')
const password = ref('')
const confirmPassword = ref('')
const verifyCode = ref('')
const recoveryCode = ref('')
const showRecovery = ref(false)

onMounted(async () => {
  try {
    const status = await api.auth.status()
    if (status.authenticated && status.user) {
      router.push('/dashboard')
    } else {
      mode.value = status.hasUsers ? 'login' : 'register'
    }
  } catch {
    mode.value = 'login'
  }
})

function clearError() {
  error.value = ''
}

async function handleRegister() {
  clearError()
  if (!email.value || !password.value || !displayName.value) {
    error.value = '請填寫所有欄位'
    return
  }
  if (password.value.length < 6) {
    error.value = '密碼長度至少 6 位'
    return
  }
  if (password.value !== confirmPassword.value) {
    error.value = '兩次密碼不一致'
    return
  }
  try {
    const res = await api.auth.register({
      email: email.value,
      displayName: displayName.value,
      password: password.value,
    })
    if (res.token && res.user) {
      auth.setAuth(res.token, res.user)
      router.push('/dashboard')
    }
  } catch (e: any) {
    error.value = e.message || '註冊失敗'
  }
}

async function handleLogin() {
  clearError()
  if (!email.value || !password.value) {
    error.value = '請填寫 Email 和密碼'
    return
  }
  try {
    const res = await api.auth.login({
      email: email.value,
      password: password.value,
    })
    if (res.requiresTwoFactor && res.tempToken) {
      tempToken.value = res.tempToken
      mode.value = 'verify-2fa'
    } else if (res.token && res.user) {
      auth.setAuth(res.token, res.user)
      router.push('/dashboard')
    }
  } catch (e: any) {
    error.value = e.message || '登入失敗'
  }
}

async function handleVerify2fa() {
  clearError()
  if (!verifyCode.value) {
    error.value = '請輸入驗證碼'
    return
  }
  try {
    const res = await api.auth.verify2fa({
      tempToken: tempToken.value,
      code: verifyCode.value,
    })
    if (res.token && res.user) {
      auth.setAuth(res.token, res.user)
      router.push('/dashboard')
    }
  } catch (e: any) {
    error.value = e.message || '驗證失敗'
  }
}

async function handleRecoveryLogin() {
  clearError()
  if (!recoveryCode.value) {
    error.value = '請輸入備用碼'
    return
  }
  try {
    const res = await api.auth.recoveryLogin({
      tempToken: tempToken.value,
      recoveryCode: recoveryCode.value,
    })
    if (res.token && res.user) {
      auth.setAuth(res.token, res.user)
      router.push('/dashboard')
    }
  } catch (e: any) {
    error.value = e.message || '備用碼無效'
  }
}

function goBackToLogin() {
  mode.value = 'login'
  verifyCode.value = ''
  recoveryCode.value = ''
  showRecovery.value = false
}
</script>

<template>
  <div class="min-h-screen bg-bg-app flex items-center justify-center p-4">
    <div class="w-full max-w-sm">
      <div class="text-center mb-8">
        <div class="w-14 h-14 bg-accent-primary rounded-xl flex items-center justify-center mx-auto mb-4">
          <span class="text-white font-bold text-2xl">$</span>
        </div>
        <h1 class="text-2xl font-bold text-text-primary">MyExpenses</h1>
        <p class="text-text-secondary text-sm mt-1">個人記帳</p>
      </div>

      <div v-if="mode === 'loading'" class="text-center text-text-secondary py-8">
        載入中...
      </div>

      <div v-if="error" class="bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800 text-red-600 dark:text-red-400 rounded-lg px-4 py-3 text-sm mb-4">
        {{ error }}
      </div>

      <form v-if="mode === 'register'" @submit.prevent="handleRegister" class="bg-white dark:bg-bg-card rounded-xl p-6 shadow-sm border border-border-color space-y-4">
        <h2 class="text-lg font-semibold text-text-primary">建立管理員帳號</h2>

        <div>
          <label class="block text-sm font-medium text-text-secondary mb-1">Email</label>
          <input v-model="email" type="email" required
            class="w-full px-3 py-2 rounded-lg border border-border-color bg-white dark:bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-accent-primary focus:border-transparent outline-none"
            placeholder="user@example.com" />
        </div>

        <div>
          <label class="block text-sm font-medium text-text-secondary mb-1">顯示名稱</label>
          <input v-model="displayName" type="text" required
            class="w-full px-3 py-2 rounded-lg border border-border-color bg-white dark:bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-accent-primary focus:border-transparent outline-none"
            placeholder="你的名稱" />
        </div>

        <div>
          <label class="block text-sm font-medium text-text-secondary mb-1">密碼</label>
          <input v-model="password" type="password" required minlength="6"
            class="w-full px-3 py-2 rounded-lg border border-border-color bg-white dark:bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-accent-primary focus:border-transparent outline-none"
            placeholder="至少 6 位" />
        </div>

        <div>
          <label class="block text-sm font-medium text-text-secondary mb-1">確認密碼</label>
          <input v-model="confirmPassword" type="password" required
            class="w-full px-3 py-2 rounded-lg border border-border-color bg-white dark:bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-accent-primary focus:border-transparent outline-none" />
        </div>

        <button type="submit"
          class="w-full py-2.5 bg-accent-primary hover:bg-accent-primary-hover text-white font-medium rounded-lg text-sm transition-colors cursor-pointer">
          建立帳號
        </button>
      </form>

      <form v-if="mode === 'login'" @submit.prevent="handleLogin" class="bg-white dark:bg-bg-card rounded-xl p-6 shadow-sm border border-border-color space-y-4">
        <h2 class="text-lg font-semibold text-text-primary">登入</h2>

        <div>
          <label class="block text-sm font-medium text-text-secondary mb-1">Email</label>
          <input v-model="email" type="email" required
            class="w-full px-3 py-2 rounded-lg border border-border-color bg-white dark:bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-accent-primary focus:border-transparent outline-none"
            placeholder="user@example.com" />
        </div>

        <div>
          <label class="block text-sm font-medium text-text-secondary mb-1">密碼</label>
          <input v-model="password" type="password" required
            class="w-full px-3 py-2 rounded-lg border border-border-color bg-white dark:bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-accent-primary focus:border-transparent outline-none" />
        </div>

        <button type="submit"
          class="w-full py-2.5 bg-accent-primary hover:bg-accent-primary-hover text-white font-medium rounded-lg text-sm transition-colors cursor-pointer">
          登入
        </button>
      </form>

      <div v-if="mode === 'verify-2fa'" class="bg-white dark:bg-bg-card rounded-xl p-6 shadow-sm border border-border-color space-y-4">
        <template v-if="!showRecovery">
          <h2 class="text-lg font-semibold text-text-primary">兩步驟驗證</h2>
          <p class="text-sm text-text-secondary">請輸入 Authenticator 應用程式中的 6 位數驗證碼</p>

          <div>
            <input v-model="verifyCode" type="text" maxlength="6" inputmode="numeric"
              class="w-full px-3 py-2 rounded-lg border border-border-color bg-white dark:bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-accent-primary focus:border-transparent outline-none text-center text-lg tracking-widest"
              placeholder="000000" @keyup.enter="handleVerify2fa" />
          </div>

          <button @click="handleVerify2fa"
            class="w-full py-2.5 bg-accent-primary hover:bg-accent-primary-hover text-white font-medium rounded-lg text-sm transition-colors cursor-pointer">
            驗證
          </button>

          <button @click="showRecovery = true"
            class="w-full text-sm text-accent-primary hover:underline text-center cursor-pointer bg-transparent border-none">
            使用備用碼登入
          </button>
        </template>

        <template v-else>
          <h2 class="text-lg font-semibold text-text-primary">備用碼登入</h2>

          <div>
            <input v-model="recoveryCode" type="text"
              class="w-full px-3 py-2 rounded-lg border border-border-color bg-white dark:bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-accent-primary focus:border-transparent outline-none"
              placeholder="XXXX-XXXX-XXXX" @keyup.enter="handleRecoveryLogin" />
          </div>

          <button @click="handleRecoveryLogin"
            class="w-full py-2.5 bg-accent-primary hover:bg-accent-primary-hover text-white font-medium rounded-lg text-sm transition-colors cursor-pointer">
            使用備用碼登入
          </button>

          <button @click="showRecovery = false"
            class="w-full text-sm text-accent-primary hover:underline text-center cursor-pointer bg-transparent border-none">
            返回驗證碼輸入
          </button>
        </template>

        <button @click="goBackToLogin"
          class="w-full text-sm text-text-secondary hover:text-text-primary text-center cursor-pointer bg-transparent border-none">
          返回登入
        </button>
      </div>
    </div>
  </div>
</template>
