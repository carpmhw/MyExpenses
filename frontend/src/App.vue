<script setup lang="ts">
import { provide, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import MainLayout from './layouts/MainLayout.vue'
import ToastContainer from './components/ui/ToastContainer.vue'
import { useToast } from './composables/useToast'
import { useDarkMode } from './composables/useDarkMode'
import { useAuth } from './composables/useAuth'

const route = useRoute()
const toast = useToast()
const auth = useAuth()
provide('toast', toast)

const darkMode = useDarkMode()
provide('darkMode', darkMode)

onMounted(() => {
  auth.fetchStatus()
})
</script>

<template>
  <router-view v-if="route.meta?.public" />
  <MainLayout v-else />
  <ToastContainer />
</template>
