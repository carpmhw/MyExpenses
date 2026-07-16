<script setup lang="ts">
import {
  DialogRoot, DialogPortal, DialogOverlay,
  DialogContent, DialogTitle, DialogClose,
} from 'radix-vue'

withDefaults(defineProps<{
  open: boolean
  title?: string
  size?: 'sm' | 'md' | 'lg' | 'xl'
  mobileFullScreen?: boolean
  scrollBody?: boolean
}>(), {
  size: 'md',
  mobileFullScreen: false,
  scrollBody: false,
})

const emit = defineEmits<{ 'update:open': [value: boolean] }>()
</script>

<template>
  <DialogRoot :open="open" @update:open="emit('update:open', $event)">
    <DialogPortal>
      <DialogOverlay class="fixed inset-0 bg-black/40 data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 z-50" />
      <DialogContent
        :class="[
          'fixed left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 w-[calc(100vw-2rem)] bg-bg-card rounded-2xl shadow-lg p-6 z-50',
          'data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95',
          'max-h-[85vh]',
          scrollBody ? 'flex flex-col overflow-hidden' : 'overflow-y-auto',
          size === 'sm' ? 'max-w-sm' : size === 'lg' ? 'max-w-2xl' : size === 'xl' ? 'max-w-5xl' : 'max-w-md',
          mobileFullScreen ? 'max-md:inset-0 max-md:translate-x-0 max-md:translate-y-0 max-md:w-screen max-md:h-[100dvh] max-md:max-h-[100dvh] max-md:max-w-none max-md:rounded-none max-md:p-4' : '',
        ]"
      >
        <div class="flex items-center justify-between mb-4 shrink-0">
          <DialogTitle class="text-lg font-semibold text-text-primary">{{ title }}</DialogTitle>
          <DialogClose aria-label="關閉" class="text-text-tertiary hover:text-text-primary transition-colors cursor-pointer">
            <svg width="20" height="20" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M5 5l10 10M15 5l-10 10" />
            </svg>
          </DialogClose>
        </div>
        <div v-if="scrollBody" class="min-h-0 flex-1 overflow-y-auto">
          <slot />
        </div>
        <slot v-else />
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
