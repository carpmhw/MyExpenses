<script setup lang="ts">
import { inject, computed, type Ref } from 'vue'
import type { Toast } from '../../composables/useToast'

const toast = inject<{ toasts: Ref<Toast[]>; dismiss: (id: number) => void }>('toast')!

const toasts = computed(() => toast.toasts.value)
</script>

<template>
  <div
    v-if="toasts.length > 0"
    style="position: fixed; bottom: 16px; right: 16px; z-index: 9999; display: flex; flex-direction: column; gap: 8px; max-width: 384px;"
  >
    <div
      v-for="t in toasts"
      :key="t.id"
      :role="t.type === 'error' ? 'alert' : 'status'"
      :aria-live="t.type === 'error' ? 'assertive' : 'polite'"
      aria-atomic="true"
      :class="[
        'flex items-center gap-3 px-4 py-3 rounded-lg border border-transparent shadow-lg text-sm',
        t.type === 'success'
          ? 'bg-color-income-action text-color-income-action-text'
          : t.type === 'error'
            ? 'bg-color-expense-toast text-color-expense-toast-text'
            : t.type === 'warning'
              ? 'bg-color-warning-action text-color-warning-action-text'
              : 'bg-color-info-action text-color-info-action-text',
      ]"
    >
      <span class="min-w-0 flex-1">{{ t.message }}</span>
      <button
        type="button"
        aria-label="關閉通知"
        class="shrink-0 min-h-11 min-w-11 inline-flex items-center justify-center rounded-md hover:bg-black/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-current cursor-pointer"
        @click="toast?.dismiss(t.id)"
      >
        <span aria-hidden="true">×</span>
      </button>
    </div>
  </div>
</template>
