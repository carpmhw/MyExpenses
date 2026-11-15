<script setup lang="ts">
import { inject, nextTick, ref, watch } from 'vue'
import type { useToast } from '../../composables/useToast'

type ToastApi = Pick<ReturnType<typeof useToast>, 'toasts' | 'dismiss' | 'hold' | 'release'>
type NotifyingDocument = Document & {
  ariaNotify?: (message: string, options?: { priority?: 'normal' | 'high' }) => void
}

const toast = inject<ToastApi>('toast')!

/** 瀏覽器支援 ariaNotify 時直接通知螢幕閱讀器；否則改用頁面載入時就存在的 live region。 */
const notifyingDocument = document as NotifyingDocument
const canNotify = typeof notifyingDocument.ariaNotify === 'function'
const politeAnnouncement = ref('')
const assertiveAnnouncement = ref('')
const region = ref<HTMLElement | null>(null)
let returnFocus: HTMLElement | null = null

/** 只朗讀通知文字（不含關閉按鈕名稱）；錯誤通知使用高優先權／assertive。 */
async function announce(message: string, urgent: boolean) {
  if (canNotify) {
    notifyingDocument.ariaNotify?.(message, { priority: urgent ? 'high' : 'normal' })
    return
  }
  const target = urgent ? assertiveAnnouncement : politeAnnouncement
  target.value = ''
  await nextTick()
  target.value = message
}

// 只朗讀新加入的通知；清空時一併清掉備援 live region，避免瀏覽模式讀到過期訊息。
watch(() => toast.toasts.value.map(t => t.id), (ids, previousIds = []) => {
  for (const t of toast.toasts.value) {
    if (ids.includes(t.id) && !previousIds.includes(t.id)) void announce(t.message, t.type === 'error')
  }
  if (ids.length === 0) {
    politeAnnouncement.value = ''
    assertiveAnnouncement.value = ''
  }
})

/** 焦點從頁面進入通知區時記住原位置，並暫停該則通知的自動關閉。 */
function handleFocusIn(event: FocusEvent, id: number) {
  const from = event.relatedTarget
  if (from instanceof HTMLElement && !region.value?.contains(from)) returnFocus = from
  toast.hold(id)
}

/** 焦點離開某則通知後恢復其自動關閉計時。 */
function handleFocusOut(event: FocusEvent, id: number) {
  const to = event.relatedTarget
  if (!(to instanceof Node) || !(event.currentTarget as HTMLElement).contains(to)) toast.release(id)
}

/** 手動關閉通知；若焦點原本在通知內，移到下一則通知，沒有通知時回到進入前的位置。 */
async function dismissFromButton(id: number, index: number) {
  const hadFocus = region.value?.contains(document.activeElement) ?? false
  toast.dismiss(id)
  if (!hadFocus) return
  await nextTick()
  const buttons = region.value?.querySelectorAll<HTMLButtonElement>('button[data-toast-close]') ?? []
  const next = buttons[Math.min(index, buttons.length - 1)]
  if (next) {
    next.focus()
  } else if (returnFocus?.isConnected) {
    returnFocus.focus()
  }
}
</script>

<template>
  <section
    ref="region"
    aria-label="通知"
    class="pointer-events-none fixed inset-x-4 bottom-4 z-[9999] flex flex-col gap-2 sm:left-auto sm:w-96"
  >
    <div
      v-for="(t, index) in toast.toasts.value"
      :key="t.id"
      :class="[
        'pointer-events-auto flex items-center gap-3 px-4 py-3 rounded-lg border border-transparent shadow-lg text-sm',
        t.type === 'success'
          ? 'bg-color-income-action text-color-income-action-text'
          : t.type === 'error'
            ? 'bg-color-expense-toast text-color-expense-toast-text'
            : t.type === 'warning'
              ? 'bg-color-warning-action text-color-warning-action-text'
              : 'bg-color-info-action text-color-info-action-text',
      ]"
      @focusin="handleFocusIn($event, t.id)"
      @focusout="handleFocusOut($event, t.id)"
    >
      <span class="min-w-0 flex-1 break-words">{{ t.message }}</span>
      <button
        type="button"
        data-toast-close
        aria-label="關閉通知"
        class="shrink-0 min-h-11 min-w-11 inline-flex items-center justify-center rounded-md hover:bg-black/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-current cursor-pointer"
        @click="dismissFromButton(t.id, index)"
      >
        <span aria-hidden="true">×</span>
      </button>
    </div>
  </section>
  <template v-if="!canNotify">
    <div class="sr-only" aria-live="polite" data-toast-announcer="polite">{{ politeAnnouncement }}</div>
    <div class="sr-only" aria-live="assertive" data-toast-announcer="assertive">{{ assertiveAnnouncement }}</div>
  </template>
</template>
