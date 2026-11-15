<script setup lang="ts">
import { useId } from 'vue'

defineProps<{
  /** 表格的可存取名稱：作為視覺隱藏的 <caption>，同時命名可捲動區域。 */
  caption: string
  columns: { key: string; label: string; align?: 'left' | 'right' | 'center' }[]
  loading?: boolean
  items?: unknown[]
  error?: string | null
  refreshing?: boolean
  retry?: () => void | Promise<void>
}>()

const captionId = useId()
</script>

<template>
  <!-- 狀態區常駐於 DOM，只切換內容，螢幕閱讀器才會朗讀「重新整理中」。 -->
  <div role="status" :class="refreshing ? 'mb-2 text-xs text-text-tertiary' : 'sr-only'">
    {{ refreshing ? '重新整理中...' : '' }}
  </div>
  <div v-if="error && items && items.length > 0" role="alert" class="mb-2 flex items-center justify-between gap-3 rounded-lg bg-color-warning-bg px-3 py-2 text-xs text-color-warning-text">
    <span>{{ error }}</span>
    <button type="button" class="underline underline-offset-2 cursor-pointer" @click="retry?.()">重試</button>
  </div>
  <!-- 可捲動區域需可聚焦並具名稱，讓鍵盤使用者能左右捲動寬表格。 -->
  <div
    role="region"
    tabindex="0"
    :aria-labelledby="captionId"
    class="overflow-x-auto rounded-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-focus-ring"
  >
    <table class="w-full text-sm" :aria-busy="loading || refreshing ? 'true' : undefined">
      <caption :id="captionId" class="sr-only">{{ caption }}</caption>
      <thead>
        <tr class="border-b border-border-default">
          <th
            v-for="col in columns"
            :key="col.key"
            scope="col"
            :class="['py-3 px-4 text-text-secondary font-medium', col.align === 'right' ? 'text-right' : col.align === 'center' ? 'text-center' : 'text-left']"
          >
            {{ col.label }}
          </th>
          <th scope="col" class="text-left py-3 px-4 text-text-secondary font-medium w-24">
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
