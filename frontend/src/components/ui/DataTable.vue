<script setup lang="ts">
defineProps<{
  columns: { key: string; label: string; align?: 'left' | 'right' | 'center' }[]
  loading?: boolean
  items?: unknown[]
  error?: string | null
  refreshing?: boolean
  retry?: () => void | Promise<void>
}>()
</script>

<template>
  <div v-if="refreshing" role="status" aria-live="polite" class="mb-2 text-xs text-text-tertiary">
    重新整理中...
  </div>
  <div v-if="error && items && items.length > 0" role="alert" class="mb-2 flex items-center justify-between gap-3 rounded-lg bg-color-warning-bg px-3 py-2 text-xs text-color-warning-text">
    <span>{{ error }}</span>
    <button type="button" class="underline underline-offset-2 cursor-pointer" @click="retry?.()">重試</button>
  </div>
  <div class="overflow-x-auto">
    <table class="w-full text-sm">
      <thead>
        <tr class="border-b border-border-default">
          <th
            v-for="col in columns"
            :key="col.key"
            :class="['py-3 px-4 text-text-secondary font-medium', col.align === 'right' ? 'text-right' : col.align === 'center' ? 'text-center' : 'text-left']"
          >
            {{ col.label }}
          </th>
          <th class="text-left py-3 px-4 text-text-secondary font-medium w-24">
            操作
          </th>
        </tr>
      </thead>
      <tbody>
        <tr v-if="loading && (!items || items.length === 0)" class="border-b border-border-default">
          <td :colspan="columns.length + 1" class="py-8 text-center text-text-tertiary">
            載入中...
          </td>
        </tr>
        <tr v-else-if="error && (!items || items.length === 0)" class="border-b border-border-default">
          <td :colspan="columns.length + 1" class="py-8 text-center text-color-expense-text" role="alert">
            <p>{{ error }}</p>
            <button type="button" class="mt-3 underline underline-offset-2 cursor-pointer" @click="retry?.()">重試</button>
          </td>
        </tr>
        <tr v-else-if="items && items.length === 0 && $slots.empty">
          <td :colspan="columns.length + 1" class="py-8">
            <slot name="empty" />
          </td>
        </tr>
        <slot v-else />
      </tbody>
    </table>
  </div>
</template>
