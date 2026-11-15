<script setup lang="ts">
import { nextTick, ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError, api } from '../../api'
import { useAuth } from '../../composables/useAuth'

const router = useRouter()
const auth = useAuth()

type Mode = 'loading' | 'register' | 'login' | 'verify-2fa'

const mode = ref<Mode>('loading')
const error = ref('')
const tempToken = ref('')

const email = ref('')
const displayName = ref('')
const bootstrapSecret = ref('')
const password = ref('')
const confirmPassword = ref('')
const verifyCode = ref('')
const recoveryCode = ref('')
const showRecovery = ref(false)
const verifyCodeInput = ref<HTMLInputElement | null>(null)
const recoveryCodeInput = ref<HTMLInputElement | null>(null)

// 切換驗證碼／備用碼表單後，把焦點移到新步驟的輸入欄位，讓鍵盤與螢幕閱讀器使用者直接接續操作。
async function focusVerificationInput() {
  await nextTick()
  const input = showRecovery.value ? recoveryCodeInput.value : verifyCodeInput.value
  input?.focus()
}

// 在一般驗證碼與備用碼表單間切換，並清除前一個步驟留下的錯誤訊息。
function switchRecovery(value: boolean) {
  clearError()
  showRecovery.value = value
  void focusVerificationInput()
}

onMounted(async () => {
  try {
    const status = await auth.initialize()
    if (status.authenticated && status.user) {
      router.push('/dashboard')
    } else {
      mode.value = auth.hasUsers.value ? 'login' : 'register'
    }
  } catch {
    mode.value = 'login'
  }
})

// Clears the current authentication form error before a new action.
function clearError() {
  error.value = ''
}

// 驗證並透過中央 API 送出首次 owner 註冊，bootstrap secret 僅存在本次表單記憶體。
async function handleRegister() {
  clearError()
  if (!email.value || !password.value || !displayName.value || !bootstrapSecret.value) {
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
    }, bootstrapSecret.value)
    if (res.token && res.user) {
      bootstrapSecret.value = ''
      auth.setAuth(res.token, res.user)
      router.push('/dashboard')
    }
  } catch (e) {
    error.value = e instanceof ApiError ? e.userMessage : '註冊失敗'
  }
}

// Validates credentials and advances to password or two-factor authentication.
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
      void focusVerificationInput()
    } else if (res.token && res.user) {
      auth.setAuth(res.token, res.user)
      router.push('/dashboard')
    }
  } catch (e) {
    error.value = e instanceof ApiError ? e.userMessage : '登入失敗'
  }
}

// Submits the temporary two-factor token and verification code.
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
  } catch (e) {
    error.value = e instanceof ApiError ? e.userMessage : '驗證失敗'
  }
}

// Uses a recovery code to complete a two-factor login.
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
  } catch (e) {
    error.value = e instanceof ApiError ? e.userMessage : '備用碼無效'
  }
}

// Resets the two-factor view back to the ordinary login form.
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
        <img src="/favicon.svg" alt="MyExpenses Logo" class="w-14 h-14 mx-auto mb-4" />
        <h1 class="text-2xl font-bold text-text-primary">MyExpenses</h1>
        <p class="text-text-secondary text-sm mt-1">個人記帳</p>
      </div>

      <div v-if="mode === 'loading'" class="text-center text-text-secondary py-8">
        載入中...
      </div>

      <div v-if="error" role="alert" class="bg-color-expense-bg border border-color-expense-text text-color-expense-text rounded-lg px-4 py-3 text-sm mb-4">
        {{ error }}
      </div>

      <form v-if="mode === 'register'" @submit.prevent="handleRegister" aria-labelledby="register-heading" class="bg-bg-card rounded-xl p-6 shadow-sm border border-border-default space-y-4">
        <h2 id="register-heading" class="text-lg font-semibold text-text-primary">建立管理員帳號</h2>

        <div>
          <label for="register-email" class="block text-sm font-medium text-text-secondary mb-1">Email</label>
          <input id="register-email" v-model="email" name="email" type="email" required
            autocomplete="username" spellcheck="false"
            class="w-full px-3 py-2 rounded-lg border border-border-strong bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-focus-ring focus:border-transparent outline-none"
            placeholder="user@example.com" />
        </div>

        <div>
          <label for="register-display-name" class="block text-sm font-medium text-text-secondary mb-1">顯示名稱</label>
          <input id="register-display-name" v-model="displayName" name="displayName" type="text" required
            autocomplete="nickname"
            class="w-full px-3 py-2 rounded-lg border border-border-strong bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-focus-ring focus:border-transparent outline-none"
            placeholder="你的名稱" />
        </div>

        <div>
          <label for="register-bootstrap-secret" class="block text-sm font-medium text-text-secondary mb-1">初始化密鑰</label>
          <input id="register-bootstrap-secret" v-model="bootstrapSecret" name="bootstrapSecret" type="password" required autocomplete="new-password"
            aria-describedby="register-bootstrap-secret-hint"
            class="w-full px-3 py-2 rounded-lg border border-border-strong bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-focus-ring focus:border-transparent outline-none"
            placeholder="輸入部署時設定的 Bootstrap secret" />
          <p id="register-bootstrap-secret-hint" class="text-xs text-text-secondary mt-1">此密鑰只會送出至伺服器，不會儲存在瀏覽器。</p>
        </div>

        <div>
          <label for="register-password" class="block text-sm font-medium text-text-secondary mb-1">密碼</label>
          <input id="register-password" v-model="password" name="password" type="password" required minlength="6"
            autocomplete="new-password" aria-describedby="register-password-hint"
            class="w-full px-3 py-2 rounded-lg border border-border-strong bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-focus-ring focus:border-transparent outline-none" />
          <p id="register-password-hint" class="text-xs text-text-secondary mt-1">至少 6 位。</p>
        </div>

        <div>
          <label for="register-confirm-password" class="block text-sm font-medium text-text-secondary mb-1">確認密碼</label>
          <input id="register-confirm-password" v-model="confirmPassword" name="confirmPassword" type="password" required minlength="6"
            autocomplete="new-password"
            class="w-full px-3 py-2 rounded-lg border border-border-strong bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-focus-ring focus:border-transparent outline-none" />
        </div>

        <button type="submit"
          class="w-full py-2.5 bg-accent-primary hover:bg-accent-primary-hover text-text-on-accent font-medium rounded-lg text-sm transition-colors cursor-pointer">
          建立帳號
        </button>
      </form>

      <form v-if="mode === 'login'" @submit.prevent="handleLogin" aria-labelledby="login-heading" class="bg-bg-card rounded-xl p-6 shadow-sm border border-border-default space-y-4">
        <h2 id="login-heading" class="text-lg font-semibold text-text-primary">登入</h2>

        <div>
          <label for="login-email" class="block text-sm font-medium text-text-secondary mb-1">Email</label>
          <input id="login-email" v-model="email" name="email" type="email" required
            autocomplete="username" spellcheck="false"
            class="w-full px-3 py-2 rounded-lg border border-border-strong bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-focus-ring focus:border-transparent outline-none"
            placeholder="user@example.com" />
        </div>

        <div>
          <label for="login-password" class="block text-sm font-medium text-text-secondary mb-1">密碼</label>
          <input id="login-password" v-model="password" name="password" type="password" required
            autocomplete="current-password"
            class="w-full px-3 py-2 rounded-lg border border-border-strong bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-focus-ring focus:border-transparent outline-none" />
        </div>

        <button type="submit"
          class="w-full py-2.5 bg-accent-primary hover:bg-accent-primary-hover text-text-on-accent font-medium rounded-lg text-sm transition-colors cursor-pointer">
          登入
        </button>
      </form>

      <div v-if="mode === 'verify-2fa'" class="bg-bg-card rounded-xl p-6 shadow-sm border border-border-default space-y-4">
        <form v-if="!showRecovery" @submit.prevent="handleVerify2fa" aria-labelledby="verify-2fa-heading" class="space-y-4">
          <h2 id="verify-2fa-heading" class="text-lg font-semibold text-text-primary">兩步驟驗證</h2>

          <div>
            <label for="verify-2fa-code" class="block text-sm text-text-secondary mb-1">請輸入 Authenticator 應用程式中的 6 位數驗證碼</label>
            <input id="verify-2fa-code" ref="verifyCodeInput" v-model="verifyCode" name="code" type="text" required
              maxlength="6" pattern="\d{6}" inputmode="numeric" autocomplete="one-time-code"
              autocapitalize="none" spellcheck="false" enterkeyhint="done"
              class="w-full px-3 py-2 rounded-lg border border-border-strong bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-focus-ring focus:border-transparent outline-none text-center text-lg tracking-widest"
              placeholder="000000" />
          </div>

          <button type="submit"
            class="w-full py-2.5 bg-accent-primary hover:bg-accent-primary-hover text-text-on-accent font-medium rounded-lg text-sm transition-colors cursor-pointer">
            驗證
          </button>

          <button type="button" @click="switchRecovery(true)"
            class="w-full text-sm text-accent-primary hover:underline text-center cursor-pointer bg-transparent border-none">
            使用備用碼登入
          </button>
        </form>

        <form v-else @submit.prevent="handleRecoveryLogin" aria-labelledby="recovery-heading" class="space-y-4">
          <h2 id="recovery-heading" class="text-lg font-semibold text-text-primary">備用碼登入</h2>

          <div>
            <label for="recovery-code" class="block text-sm text-text-secondary mb-1">備用碼</label>
            <input id="recovery-code" ref="recoveryCodeInput" v-model="recoveryCode" name="recoveryCode" type="text" required
              autocomplete="off" autocapitalize="characters" spellcheck="false" enterkeyhint="done"
              class="w-full px-3 py-2 rounded-lg border border-border-strong bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-focus-ring focus:border-transparent outline-none"
              placeholder="XXXX-XXXX-XXXX" />
          </div>

          <button type="submit"
            class="w-full py-2.5 bg-accent-primary hover:bg-accent-primary-hover text-text-on-accent font-medium rounded-lg text-sm transition-colors cursor-pointer">
            使用備用碼登入
          </button>

          <button type="button" @click="switchRecovery(false)"
            class="w-full text-sm text-accent-primary hover:underline text-center cursor-pointer bg-transparent border-none">
            返回驗證碼輸入
          </button>
        </form>

        <button type="button" @click="goBackToLogin"
          class="w-full text-sm text-text-secondary hover:text-text-primary text-center cursor-pointer bg-transparent border-none">
          返回登入
        </button>
      </div>
    </div>
  </div>
</template>
