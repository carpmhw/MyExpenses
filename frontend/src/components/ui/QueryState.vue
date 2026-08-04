<script setup lang="ts">
import type { AsyncQueryStatus } from '../../composables/useAsyncQuery'

withDefaults(defineProps<{
  status: AsyncQueryStatus
  errorMessage?: string
  emptyMessage?: string
  lastSuccessAt?: number | null
  retry?: () => void | Promise<void>
}>(), {
  errorMessage: '載入失敗，請重試。',
  emptyMessage: '尚無資料',
  lastSuccessAt: null,
})

// Formats the last successful timestamp without claiming a failed request just completed.
function formatLastSuccess(timestamp: number | null): string {
  if (timestamp === null) return ''
  return `上次成功更新：${new Date(timestamp).toLocaleString('zh-TW')}`
}
</script>

<template>
  <div v-if="status === 'loading'" role="status" aria-live="polite" class="py-8 text-center text-text-tertiary">
    載入中...
  </div>
  <div v-else-if="status === 'error'" role="alert" class="py-8 text-center text-color-expense-text">
    <p>{{ errorMessage }}</p>
    <button
      type="button"
      class="mt-3 px-3 py-1.5 rounded-lg text-sm text-accent-primary hover:bg-bg-raised focus:outline-none focus:ring-2 focus:ring-focus-ring cursor-pointer"
      @click="retry?.()"
    >
      重試
    </button>
  </div>
  <div v-else-if="status === 'empty'" role="status" aria-live="polite" class="py-8 text-center text-text-tertiary">
    {{ emptyMessage }}
  </div>
  <div v-else>
    <div v-if="status === 'refreshing'" role="status" aria-live="polite" class="mb-2 text-xs text-text-tertiary">
      重新整理中...
    </div>
    <div v-if="status === 'stale'" role="status" aria-live="polite" class="mb-2 flex items-center justify-between gap-3 rounded-lg bg-color-warning-bg px-3 py-2 text-xs text-color-warning-text">
      <span>資料可能已過期<span v-if="formatLastSuccess(lastSuccessAt)">，{{ formatLastSuccess(lastSuccessAt) }}</span></span>
      <button type="button" class="underline underline-offset-2 cursor-pointer" @click="retry?.()">重試</button>
    </div>
    <slot />
  </div>
</template>
