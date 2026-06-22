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
      :style="{
        display: 'flex',
        alignItems: 'center',
        gap: '12px',
        padding: '12px 16px',
        borderRadius: '8px',
        boxShadow: '0 10px 15px -3px rgb(0 0 0 / 0.1)',
        fontSize: '14px',
        color: '#fff',
        cursor: 'pointer',
        backgroundColor: t.type === 'success' ? '#10B981' : t.type === 'error' ? '#E11D48' : t.type === 'warning' ? '#F59E0B' : '#6366F1',
      }"
      @click="toast?.dismiss(t.id)"
    >
      <span>{{ t.message }}</span>
    </div>
  </div>
</template>
