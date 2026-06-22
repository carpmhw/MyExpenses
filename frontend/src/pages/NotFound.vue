<script setup lang="ts">
import { useRouter } from 'vue-router'

const router = useRouter()
</script>

<template>
  <div class="flex items-center justify-center h-full">
    <div class="text-center">
      <p class="text-6xl font-bold text-text-tertiary mb-4">404</p>
      <p class="text-lg text-text-secondary mb-6">找不到頁面</p>
      <button
        class="px-4 py-2 bg-accent-primary text-white rounded-lg text-sm cursor-pointer hover:bg-accent-primary-hover transition-colors"
        @click="router.push('/dashboard')"
      >
        返回儀表板
      </button>
    </div>
  </div>
</template>
