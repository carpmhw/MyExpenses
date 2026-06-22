<script setup lang="ts">
import { ref, computed, inject, onMounted, nextTick } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '../../api'
import type { ApiToken } from '../../types'
import { useAuth } from '../../composables/useAuth'
import QRCode from 'qrcode'

const router = useRouter()
const auth = useAuth()
const toast = inject<{ success: (m: string) => void; error: (m: string) => void }>('toast')!

const displayName = ref(auth.user.value?.displayName || '')
const currentPassword = ref('')
const newPassword = ref('')
const confirmNewPassword = ref('')
const saving = ref(false)
const changingPassword = ref(false)

const twoFactorEnabled = ref(auth.user.value?.isTwoFactorEnabled || false)
const setupMode = ref(false)
const setupSecret = ref('')
const setupUri = ref('')
const qrCodeDataUrl = ref('')
const verifyCode = ref('')
const verifying = ref(false)
const recoveryCodes = ref<string[]>([])
const showRecoveryCodes = ref(false)

const tokens = ref<ApiToken[]>([])
const newTokenName = ref('')
const creatingToken = ref(false)
const newlyCreatedToken = ref<{ id: number; name: string; prefix: string; createdAt: string; token: string } | null>(null)
const revokingToken = ref<ApiToken | null>(null)
const revoking = ref(false)

const activeTokens = computed(() => tokens.value.filter(t => !t.isRevoked))

function formatDate(dateStr: string): string {
  const d = new Date(dateStr)
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')} ${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`
}

async function fetchTokens() {
  try {
    tokens.value = await api.apiTokens.list()
  } catch (e: any) {
    toast.error(e.message || '取得金鑰列表失敗')
  }
}

async function createToken() {
  if (!newTokenName.value.trim()) {
    toast.error('請輸入金鑰名稱')
    return
  }
  creatingToken.value = true
  try {
    const res = await api.apiTokens.create(newTokenName.value.trim())
    newlyCreatedToken.value = res
    newTokenName.value = ''
    await fetchTokens()
    toast.success('金鑰已建立')
  } catch (e: any) {
    toast.error(e.message || '建立金鑰失敗')
  } finally {
    creatingToken.value = false
  }
}

async function copyToken() {
  if (!newlyCreatedToken.value) return
  try {
    await navigator.clipboard.writeText(newlyCreatedToken.value.token)
    toast.success('已複製到剪貼簿')
  } catch {
    toast.error('複製失敗')
  }
}

async function revoke() {
  if (!revokingToken.value) return
  revoking.value = true
  try {
    await api.apiTokens.revoke(revokingToken.value.id)
    tokens.value = tokens.value.map(t =>
      t.id === revokingToken.value!.id ? { ...t, isRevoked: true } : t
    )
    revokingToken.value = null
    toast.success('金鑰已撤銷')
  } catch (e: any) {
    toast.error(e.message || '撤銷失敗')
  } finally {
    revoking.value = false
  }
}

async function saveProfile() {
  if (!displayName.value) return
  saving.value = true
  try {
    const updated = await api.auth.updateProfile({ displayName: displayName.value })
    if (auth.user.value) {
      auth.user.value.displayName = updated.displayName
    }
    toast.success('顯示名稱已更新')
  } catch (e: any) {
    toast.error(e.message || '更新失敗')
  } finally {
    saving.value = false
  }
}

async function changePassword() {
  if (!currentPassword.value || !newPassword.value) {
    toast.error('請填寫所有密碼欄位')
    return
  }
  if (newPassword.value.length < 6) {
    toast.error('新密碼長度至少 6 位')
    return
  }
  if (newPassword.value !== confirmNewPassword.value) {
    toast.error('兩次新密碼不一致')
    return
  }
  changingPassword.value = true
  try {
    await api.auth.changePassword({
      currentPassword: currentPassword.value,
      newPassword: newPassword.value,
    })
    toast.success('密碼已更新')
    currentPassword.value = ''
    newPassword.value = ''
    confirmNewPassword.value = ''
  } catch (e: any) {
    toast.error(e.message || '密碼更新失敗')
  } finally {
    changingPassword.value = false
  }
}

async function setup2fa() {
  try {
    const res = await api.auth.setup2fa()
    setupSecret.value = res.secret
    setupUri.value = res.uri
    setupMode.value = true
    qrCodeDataUrl.value = await QRCode.toDataURL(res.uri, { width: 200, margin: 2 })
    await nextTick()
  } catch (e: any) {
    toast.error(e.message || '設定失敗')
  }
}

async function verify2faSetup() {
  if (!verifyCode.value) {
    toast.error('請輸入驗證碼')
    return
  }
  verifying.value = true
  try {
    const res = await api.auth.verify2faSetup({ code: verifyCode.value })
    twoFactorEnabled.value = true
    recoveryCodes.value = res.recoveryCodes
    showRecoveryCodes.value = true
    setupMode.value = false
    if (auth.user.value) {
      auth.user.value.isTwoFactorEnabled = true
    }
    toast.success('兩步驟驗證已啟用')
  } catch (e: any) {
    toast.error(e.message || '驗證失敗')
  } finally {
    verifying.value = false
  }
}

async function disable2fa() {
  try {
    await api.auth.disable2fa()
    twoFactorEnabled.value = false
    showRecoveryCodes.value = false
    recoveryCodes.value = []
    if (auth.user.value) {
      auth.user.value.isTwoFactorEnabled = false
    }
    toast.success('兩步驟驗證已停用')
  } catch (e: any) {
    toast.error(e.message || '停用失敗')
  }
}

async function regenerateRecoveryCodes() {
  try {
    const res = await api.auth.getRecoveryCodes()
    recoveryCodes.value = res.recoveryCodes
    showRecoveryCodes.value = true
    toast.success('已產生新的備用碼')
  } catch (e: any) {
    toast.error(e.message || '產生失敗')
  }
}

async function logoutAll() {
  try {
    await api.auth.logoutAll()
    auth.logout()
    router.push('/login')
  } catch (e: any) {
    toast.error(e.message || '操作失敗')
  }
}

async function logout() {
  await auth.logout()
  router.push('/login')
}

onMounted(() => {
  displayName.value = auth.user.value?.displayName || ''
  twoFactorEnabled.value = auth.user.value?.isTwoFactorEnabled || false
  fetchTokens()
})
</script>

<template>
  <div class="p-6 max-w-2xl mx-auto space-y-6">
    <h1 class="text-2xl font-bold text-text-primary">使用者設定</h1>

    <section class="bg-white dark:bg-bg-card rounded-xl p-6 shadow-sm border border-border-color space-y-4">
      <h2 class="text-lg font-semibold text-text-primary">個人資料</h2>

      <div>
        <label class="block text-sm font-medium text-text-secondary mb-1">Email</label>
        <p class="text-text-primary text-sm py-2">{{ auth.user.value?.email }}</p>
      </div>

      <div>
        <label class="block text-sm font-medium text-text-secondary mb-1">顯示名稱</label>
        <div class="flex gap-2">
          <input v-model="displayName" type="text"
            class="flex-1 px-3 py-2 rounded-lg border border-border-color bg-white dark:bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-accent-primary focus:border-transparent outline-none" />
          <button @click="saveProfile" :disabled="saving"
            class="px-4 py-2 bg-accent-primary hover:bg-accent-primary-hover disabled:opacity-50 text-white rounded-lg text-sm transition-colors cursor-pointer">
            {{ saving ? '儲存中...' : '儲存' }}
          </button>
        </div>
      </div>
    </section>

    <section class="bg-white dark:bg-bg-card rounded-xl p-6 shadow-sm border border-border-color space-y-4">
      <h2 class="text-lg font-semibold text-text-primary">修改密碼</h2>

      <div>
        <label class="block text-sm font-medium text-text-secondary mb-1">目前密碼</label>
        <input v-model="currentPassword" type="password"
          class="w-full px-3 py-2 rounded-lg border border-border-color bg-white dark:bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-accent-primary focus:border-transparent outline-none" />
      </div>

      <div>
        <label class="block text-sm font-medium text-text-secondary mb-1">新密碼</label>
        <input v-model="newPassword" type="password" minlength="6"
          class="w-full px-3 py-2 rounded-lg border border-border-color bg-white dark:bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-accent-primary focus:border-transparent outline-none" />
      </div>

      <div>
        <label class="block text-sm font-medium text-text-secondary mb-1">確認新密碼</label>
        <input v-model="confirmNewPassword" type="password"
          class="w-full px-3 py-2 rounded-lg border border-border-color bg-white dark:bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-accent-primary focus:border-transparent outline-none" />
      </div>

      <button @click="changePassword" :disabled="changingPassword"
        class="px-4 py-2 bg-accent-primary hover:bg-accent-primary-hover disabled:opacity-50 text-white rounded-lg text-sm transition-colors cursor-pointer">
        {{ changingPassword ? '更新中...' : '更新密碼' }}
      </button>
    </section>

    <section class="bg-white dark:bg-bg-card rounded-xl p-6 shadow-sm border border-border-color space-y-4">
      <h2 class="text-lg font-semibold text-text-primary">兩步驟驗證</h2>

      <template v-if="!twoFactorEnabled && !setupMode">
        <p class="text-sm text-text-secondary">啟用兩步驟驗證來提升帳戶安全性。</p>
        <button @click="setup2fa"
          class="px-4 py-2 bg-accent-primary hover:bg-accent-primary-hover text-white rounded-lg text-sm transition-colors cursor-pointer">
          啟用兩步驟驗證
        </button>
      </template>

      <template v-if="setupMode">
        <p class="text-sm text-text-secondary">使用 Authenticator 應用程式掃描下方 QR Code：</p>

        <div v-if="qrCodeDataUrl" class="flex justify-center py-4">
          <img :src="qrCodeDataUrl" alt="QR Code" class="w-48 h-48" />
        </div>

        <div>
          <p class="text-xs text-text-secondary mb-1">或手動輸入密鑰：</p>
          <p class="text-sm font-mono bg-bg-app rounded-lg px-3 py-2 break-all select-all">{{ setupSecret }}</p>
        </div>

        <div>
          <label class="block text-sm font-medium text-text-secondary mb-1">輸入驗證碼確認</label>
          <div class="flex gap-2">
            <input v-model="verifyCode" type="text" maxlength="6" inputmode="numeric"
              class="flex-1 px-3 py-2 rounded-lg border border-border-color bg-white dark:bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-accent-primary focus:border-transparent outline-none tracking-widest"
              placeholder="000000" />
            <button @click="verify2faSetup" :disabled="verifying"
              class="px-4 py-2 bg-accent-primary hover:bg-accent-primary-hover disabled:opacity-50 text-white rounded-lg text-sm transition-colors cursor-pointer">
              {{ verifying ? '驗證中...' : '確認' }}
            </button>
          </div>
        </div>

        <button @click="setupMode = false"
          class="text-sm text-text-secondary hover:text-text-primary cursor-pointer bg-transparent border-none">
          取消
        </button>
      </template>

      <template v-if="twoFactorEnabled">
        <div class="flex items-center gap-2 text-sm text-green-600 dark:text-green-400">
          <span class="w-2 h-2 rounded-full bg-green-500" />
          兩步驟驗證已啟用
        </div>

        <button @click="disable2fa"
          class="px-4 py-2 bg-red-500 hover:bg-red-600 text-white rounded-lg text-sm transition-colors cursor-pointer">
          停用兩步驟驗證
        </button>

        <div v-if="showRecoveryCodes && recoveryCodes.length > 0" class="bg-yellow-50 dark:bg-yellow-900/20 border border-yellow-200 dark:border-yellow-800 rounded-lg p-4">
          <p class="text-sm font-medium text-yellow-800 dark:text-yellow-200 mb-2">備用碼（請妥善保存）</p>
          <p class="text-xs text-yellow-600 dark:text-yellow-400 mb-3">每個備用碼只能使用一次。此為最後一次顯示。</p>
          <div class="grid grid-cols-1 gap-1">
            <code v-for="(code, i) in recoveryCodes" :key="i" class="block font-mono text-sm bg-white dark:bg-bg-app rounded px-3 py-1.5">{{ code }}</code>
          </div>
        </div>

        <button @click="regenerateRecoveryCodes"
          class="text-sm text-accent-primary hover:underline cursor-pointer bg-transparent border-none">
          重新產生備用碼
        </button>
      </template>
    </section>

    <section class="bg-white dark:bg-bg-card rounded-xl p-6 shadow-sm border border-border-color space-y-4">
      <h2 class="text-lg font-semibold text-text-primary">API 金鑰管理</h2>

      <div class="flex gap-2">
        <input v-model="newTokenName" type="text" placeholder="金鑰名稱"
          class="flex-1 px-3 py-2 rounded-lg border border-border-color bg-white dark:bg-bg-app text-text-primary text-sm focus:ring-2 focus:ring-accent-primary focus:border-transparent outline-none" />
        <button @click="createToken" :disabled="creatingToken"
          class="px-4 py-2 bg-accent-primary hover:bg-accent-primary-hover disabled:opacity-50 text-white rounded-lg text-sm transition-colors cursor-pointer whitespace-nowrap">
          {{ creatingToken ? '建立中...' : '建立新金鑰' }}
        </button>
      </div>

      <div v-if="newlyCreatedToken" class="bg-green-50 dark:bg-green-900/20 border border-green-200 dark:border-green-800 rounded-lg p-4 space-y-2">
        <p class="text-sm font-medium text-green-800 dark:text-green-200">金鑰已建立（僅顯示一次，請立即複製）</p>
        <div class="flex gap-2">
          <input :value="newlyCreatedToken.token" type="text" readonly
            class="flex-1 px-3 py-2 rounded-lg border border-green-300 dark:border-green-700 bg-white dark:bg-bg-app text-text-primary text-sm font-mono" />
          <button @click="copyToken"
            class="px-3 py-2 bg-green-600 hover:bg-green-700 text-white rounded-lg text-sm transition-colors cursor-pointer whitespace-nowrap">
            複製
          </button>
        </div>
        <button @click="newlyCreatedToken = null"
          class="text-sm text-text-secondary hover:text-text-primary cursor-pointer bg-transparent border-none">
          關閉
        </button>
      </div>

      <div v-if="activeTokens.length > 0" class="overflow-x-auto">
        <table class="w-full text-sm">
          <thead>
            <tr class="text-left text-text-secondary border-b border-border-color">
              <th class="pb-2 pr-4 font-medium">名稱</th>
              <th class="pb-2 pr-4 font-medium">前綴</th>
              <th class="pb-2 pr-4 font-medium">建立時間</th>
              <th class="pb-2 pr-4 font-medium">最後使用</th>
              <th class="pb-2 font-medium"></th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="token in activeTokens" :key="token.id" class="border-b border-border-color">
              <td class="py-2 pr-4 text-text-primary">{{ token.name }}</td>
              <td class="py-2 pr-4 text-text-secondary font-mono">{{ token.prefix }}...</td>
              <td class="py-2 pr-4 text-text-secondary whitespace-nowrap">{{ formatDate(token.createdAt) }}</td>
              <td class="py-2 pr-4 text-text-secondary whitespace-nowrap">{{ token.lastUsedAt ? formatDate(token.lastUsedAt) : '從未使用' }}</td>
              <td class="py-2">
                <button @click="revokingToken = token"
                  class="text-red-500 hover:text-red-700 text-sm cursor-pointer bg-transparent border-none">
                  撤銷
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
      <p v-else class="text-sm text-text-secondary">尚無 API 金鑰。</p>

      <div v-if="revokingToken" class="fixed inset-0 bg-black/50 flex items-center justify-center z-50" @click.self="revokingToken = null">
        <div class="bg-white dark:bg-bg-card rounded-xl p-6 shadow-xl max-w-sm mx-4 space-y-4">
          <p class="text-text-primary">確定要撤銷「{{ revokingToken.name }}」嗎？此操作無法復原。</p>
          <div class="flex justify-end gap-2">
            <button @click="revokingToken = null"
              class="px-4 py-2 bg-gray-200 dark:bg-gray-700 hover:bg-gray-300 dark:hover:bg-gray-600 text-text-primary rounded-lg text-sm transition-colors cursor-pointer">
              取消
            </button>
            <button @click="revoke" :disabled="revoking"
              class="px-4 py-2 bg-red-500 hover:bg-red-600 disabled:opacity-50 text-white rounded-lg text-sm transition-colors cursor-pointer">
              {{ revoking ? '撤銷中...' : '確認撤銷' }}
            </button>
          </div>
        </div>
      </div>
    </section>

    <section class="bg-white dark:bg-bg-card rounded-xl p-6 shadow-sm border border-border-color space-y-4">
      <button @click="logout"
        class="px-4 py-2 bg-gray-200 dark:bg-gray-700 hover:bg-gray-300 dark:hover:bg-gray-600 text-text-primary rounded-lg text-sm transition-colors cursor-pointer">
        登出
      </button>
      <div class="pt-3 border-t border-border-color">
        <p class="text-xs text-text-secondary mb-2">使所有已登入裝置的 token 失效（需要重新登入）</p>
        <button @click="logoutAll"
          class="px-4 py-2 bg-orange-500 hover:bg-orange-600 text-white rounded-lg text-sm transition-colors cursor-pointer">
          登出所有裝置
        </button>
      </div>
    </section>
  </div>
</template>
