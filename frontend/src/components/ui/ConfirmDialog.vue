<script setup lang="ts">
import {
  AlertDialogRoot,
  AlertDialogPortal,
  AlertDialogOverlay,
  AlertDialogContent,
  AlertDialogTitle,
  AlertDialogDescription,
  AlertDialogCancel,
  AlertDialogAction,
} from 'radix-vue'

withDefaults(defineProps<{
  open: boolean
  title?: string
  description?: string
  confirmText?: string
  variant?: 'danger' | 'warning' | 'info'
}>(), {
  variant: 'danger',
})

const emit = defineEmits<{
  'update:open': [value: boolean]
  confirm: []
}>()
</script>

<template>
  <AlertDialogRoot :open="open" @update:open="emit('update:open', $event)">
    <AlertDialogPortal>
      <AlertDialogOverlay class="fixed inset-0 bg-black/40 data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0" />
      <AlertDialogContent class="fixed left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 w-full max-w-sm bg-bg-card rounded-2xl shadow-lg p-6 data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95">
        <AlertDialogTitle class="text-lg font-semibold text-text-primary">
          {{ title || '確認' }}
        </AlertDialogTitle>
        <AlertDialogDescription class="mt-2 text-sm text-text-secondary">
          {{ description || '確定要執行此操作嗎？' }}
        </AlertDialogDescription>
        <div class="mt-6 flex justify-end gap-3">
          <AlertDialogCancel class="px-4 py-2 text-sm font-medium text-text-secondary bg-transparent border border-border-default rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 cursor-pointer transition-colors">
            取消
          </AlertDialogCancel>
          <AlertDialogAction
            :class="[
              'px-4 py-2 text-sm font-medium text-white rounded-lg cursor-pointer transition-colors',
              variant === 'warning' ? 'bg-amber-500 hover:bg-amber-600' : variant === 'info' ? 'bg-blue-500 hover:bg-blue-600' : 'bg-red-500 hover:bg-red-600',
            ]"
            @click="emit('confirm')"
          >
            {{ confirmText || '確認刪除' }}
          </AlertDialogAction>
        </div>
      </AlertDialogContent>
    </AlertDialogPortal>
  </AlertDialogRoot>
</template>
