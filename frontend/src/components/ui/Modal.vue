<script setup lang="ts">
import {
  DialogRoot, DialogPortal, DialogOverlay,
  DialogContent, DialogTitle, DialogDescription, DialogClose,
} from 'radix-vue'

const props = withDefaults(defineProps<{
  open: boolean
  title?: string
  description?: string
  size?: 'sm' | 'md' | 'lg' | 'xl'
  mobileFullScreen?: boolean
  scrollBody?: boolean
  closeDisabled?: boolean
}>(), {
  size: 'md',
  description: '對話框內容',
  mobileFullScreen: false,
  scrollBody: false,
  closeDisabled: false,
})

const emit = defineEmits<{ 'update:open': [value: boolean] }>()

// 在送出期間拒絕由 Escape、遮罩或關閉按鈕觸發的關閉事件。
function handleOpenUpdate(value: boolean) {
  if (!value && props.closeDisabled) return
  emit('update:open', value)
}
</script>

<template>
  <DialogRoot :open="props.open" @update:open="handleOpenUpdate">
    <DialogPortal>
      <DialogOverlay class="fixed inset-0 bg-black/40 data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 z-50" />
      <DialogContent
        :class="[
          'fixed left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 w-[calc(100vw-2rem)] bg-bg-card border border-border-overlay rounded-2xl shadow-lg p-6 z-50',
          'data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95',
          'max-h-[85vh]',
           props.scrollBody ? 'flex flex-col overflow-hidden' : 'overflow-y-auto',
           props.size === 'sm' ? 'max-w-sm' : props.size === 'lg' ? 'max-w-2xl' : props.size === 'xl' ? 'max-w-5xl' : 'max-w-md',
           props.mobileFullScreen ? 'max-md:inset-0 max-md:translate-x-0 max-md:translate-y-0 max-md:w-screen max-md:h-[100dvh] max-md:max-h-[100dvh] max-md:max-w-none max-md:rounded-none max-md:p-4' : '',
        ]"
      >
        <DialogDescription class="sr-only">{{ props.description }}</DialogDescription>
        <div class="flex items-center justify-between gap-3 mb-4 shrink-0">
          <DialogTitle class="min-w-0 flex-1 break-words text-lg font-semibold text-text-primary">{{ props.title }}</DialogTitle>
          <DialogClose
            aria-label="關閉"
            :disabled="props.closeDisabled"
            :aria-disabled="props.closeDisabled ? 'true' : undefined"
            :class="[
              'shrink-0 min-h-11 min-w-11 inline-flex items-center justify-center text-text-tertiary hover:text-text-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-focus-ring transition-colors',
              props.closeDisabled ? 'cursor-not-allowed opacity-50' : 'cursor-pointer',
            ]"
          >
            <svg width="20" height="20" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M5 5l10 10M15 5l-10 10" />
            </svg>
          </DialogClose>
        </div>
        <div v-if="props.scrollBody" class="min-h-0 flex-1 overflow-y-auto">
          <slot />
        </div>
        <slot v-else />
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
