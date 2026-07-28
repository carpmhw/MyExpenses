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
        ? 'bg-transparent text-text-secondary hover:bg-bg-control-hover'
        : variant === 'danger'
          ? 'bg-color-expense-action text-color-expense-action-text hover:bg-color-expense-action/90'
          : variant === 'warning'
            ? 'bg-color-warning-action text-color-warning-action-text hover:bg-color-warning-action/90'
            : 'bg-accent-primary text-text-on-accent hover:bg-accent-primary-hover',
      (disabled || loading) ? 'opacity-50 cursor-not-allowed' : 'cursor-pointer',
    ]"
  >
    <Icon v-if="loading" name="Loader2" :size="16" class="animate-spin" />
    <slot />
  </button>
</template>
