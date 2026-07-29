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
      :class="[
        'flex items-center gap-3 px-4 py-3 rounded-lg border border-transparent shadow-lg text-sm cursor-pointer',
        t.type === 'success'
          ? 'bg-color-income-action text-color-income-action-text'
          : t.type === 'error'
            ? 'bg-color-expense-toast text-color-expense-toast-text'
            : t.type === 'warning'
              ? 'bg-color-warning-action text-color-warning-action-text'
              : 'bg-color-info-action text-color-info-action-text',
      ]"
      @click="toast?.dismiss(t.id)"
    >
      <span>{{ t.message }}</span>
    </div>
  </div>
</template>
