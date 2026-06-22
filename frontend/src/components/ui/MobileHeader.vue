<script setup lang="ts">
import { inject } from 'vue'
import { Menu, Moon, Sun } from '@lucide/vue'

defineProps<{
  onMenuClick: () => void
}>()

const darkMode = inject<{ isDark: { value: boolean }; toggle: () => void }>('darkMode')!
</script>

<template>
  <div class="flex flex-col items-center py-3 w-12 bg-bg-sidebar text-white shrink-0 lg:hidden">
    <button
      class="p-2 rounded-lg hover:bg-white/10 transition-colors cursor-pointer"
      @click="onMenuClick"
    >
      <Menu :size="20" />
    </button>
    <button
      class="p-2 rounded-lg hover:bg-white/10 transition-colors cursor-pointer mt-auto"
      @click="darkMode.toggle()"
    >
      <Sun v-if="darkMode.isDark.value" :size="18" />
      <Moon v-else :size="18" />
    </button>
  </div>
</template>
