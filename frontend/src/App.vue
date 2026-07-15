<script setup lang="ts">
import { provide, onMounted, watch } from 'vue'
import { useRoute } from 'vue-router'
import MainLayout from './layouts/MainLayout.vue'
import ToastContainer from './components/ui/ToastContainer.vue'
import { useToast } from './composables/useToast'
import { useDarkMode } from './composables/useDarkMode'
import { useAuth } from './composables/useAuth'
import { useTimeZone } from './composables/useTimeZone'

const route = useRoute()
const toast = useToast()
const auth = useAuth()
const timeZone = useTimeZone()
provide('toast', toast)
provide('timeZone', timeZone)

const darkMode = useDarkMode()
provide('darkMode', darkMode)

// Loads authentication and system time zone state before authenticated pages render.
async function initializeApplication(): Promise<void> {
  await auth.fetchStatus()
  if (auth.isAuthenticated.value) await timeZone.fetchTimeZone()
}

onMounted(initializeApplication)

watch(() => auth.isAuthenticated.value, async (authenticated) => {
  if (authenticated && !timeZone.isReady.value) await timeZone.fetchTimeZone()
  if (!authenticated) timeZone.resetTimeZone()
})
</script>

<template>
  <router-view v-if="route.meta?.public" />
  <MainLayout v-else-if="!auth.isAuthenticated.value || timeZone.isReady.value" />
  <div v-else class="min-h-screen flex items-center justify-center bg-bg-app p-6">
    <div class="max-w-sm text-center space-y-3">
      <p class="text-sm text-text-secondary">
        {{ timeZone.loadError.value ? '無法載入系統時區設定。' : '載入系統時區設定中...' }}
      </p>
      <button v-if="timeZone.loadError.value" @click="timeZone.fetchTimeZone"
        class="px-4 py-2 bg-accent-primary hover:bg-accent-primary-hover text-white rounded-lg text-sm cursor-pointer">
        重試
      </button>
    </div>
  </div>
  <ToastContainer />
</template>
