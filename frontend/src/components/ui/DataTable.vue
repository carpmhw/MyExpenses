<script setup lang="ts">
defineProps<{
  columns: { key: string; label: string; align?: 'left' | 'right' | 'center' }[]
  loading?: boolean
  items?: unknown[]
}>()
</script>

<template>
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
        <tr v-if="loading" class="border-b border-border-default">
          <td :colspan="columns.length + 1" class="py-8 text-center text-text-tertiary">
            載入中...
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
