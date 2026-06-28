<script setup lang="ts">
import {
  DialogRoot, DialogPortal, DialogOverlay,
  DialogContent, DialogTitle, DialogClose,
} from 'radix-vue'

withDefaults(defineProps<{
  open: boolean
  title?: string
  size?: 'sm' | 'md' | 'lg'
}>(), { size: 'md' })

const emit = defineEmits<{ 'update:open': [value: boolean] }>()
</script>

<template>
  <DialogRoot :open="open" @update:open="emit('update:open', $event)">
    <DialogPortal>
      <DialogOverlay class="fixed inset-0 bg-black/40 data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 z-50" />
      <DialogContent
        :class="[
          'fixed left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 w-full bg-bg-card rounded-2xl shadow-lg p-6 z-50',
          'data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95',
          'max-h-[85vh] overflow-y-auto mx-4',
          size === 'sm' ? 'max-w-sm' : size === 'lg' ? 'max-w-2xl' : 'max-w-md',
        ]"
      >
        <div class="flex items-center justify-between mb-4">
          <DialogTitle class="text-lg font-semibold text-text-primary">{{ title }}</DialogTitle>
          <DialogClose class="text-text-tertiary hover:text-text-primary transition-colors cursor-pointer">
            <svg width="20" height="20" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M5 5l10 10M15 5l-10 10" />
            </svg>
          </DialogClose>
        </div>
        <slot />
      </DialogContent>
    </DialogPortal>
  </DialogRoot>
</template>
