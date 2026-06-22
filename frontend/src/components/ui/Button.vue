<script setup lang="ts">
import Icon from './Icon.vue'

withDefaults(defineProps<{
  variant?: 'primary' | 'ghost' | 'danger' | 'warning'
  disabled?: boolean
  loading?: boolean
}>(), {
  variant: 'primary',
})
</script>

<template>
  <button
    :disabled="disabled || loading"
    :class="[
      'inline-flex items-center gap-2 px-4 py-2 rounded-lg text-sm font-medium transition-colors',
      variant === 'ghost'
        ? 'bg-transparent text-text-secondary hover:bg-gray-100 dark:hover:bg-gray-700'
        : variant === 'danger'
          ? 'bg-red-500 text-white hover:bg-red-600'
          : variant === 'warning'
            ? 'bg-amber-500 text-white hover:bg-amber-600'
            : 'bg-accent-primary text-white hover:bg-accent-primary-hover',
      (disabled || loading) ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer',
    ]"
  >
    <Icon v-if="loading" name="Loader2" :size="16" class="animate-spin" />
    <slot />
  </button>
</template>
