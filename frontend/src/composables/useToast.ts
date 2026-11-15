import { ref } from 'vue'

export type ToastType = 'success' | 'error' | 'warning' | 'info'

export interface Toast {
  id: number
  type: ToastType
  message: string
}

/** 通知自動關閉前的顯示時間（毫秒）。 */
const AUTO_DISMISS_MS = 4000

let nextId = 1

/**
 * 通知佇列 composable：新增的通知會在 AUTO_DISMISS_MS 後自動關閉；
 * `hold`／`release` 讓 UI 在使用者聚焦通知時暫停、離開後重新計時，避免操作中的通知消失。
 */
export function useToast() {
  const toasts = ref<Toast[]>([])
  const timers = new Map<number, ReturnType<typeof setTimeout>>()

  /** 為指定通知啟動自動關閉計時器。 */
  function schedule(id: number) {
    timers.set(id, setTimeout(() => dismiss(id), AUTO_DISMISS_MS))
  }

  /** 新增一則通知並開始自動關閉計時。 */
  function add(type: ToastType, message: string) {
    const id = nextId++
    toasts.value.push({ id, type, message })
    schedule(id)
  }

  /** 暫停指定通知的自動關閉（例如焦點在通知內）。 */
  function hold(id: number) {
    clearTimeout(timers.get(id))
    timers.delete(id)
  }

  /** 恢復已暫停通知的自動關閉，重新完整計時；通知已不存在或仍在計時則不動作。 */
  function release(id: number) {
    if (!timers.has(id) && toasts.value.some(t => t.id === id)) schedule(id)
  }

  /** 移除指定通知並清除其計時器。 */
  function dismiss(id: number) {
    hold(id)
    const idx = toasts.value.findIndex(t => t.id === id)
    if (idx !== -1) toasts.value.splice(idx, 1)
  }

  return {
    toasts,
    success: (message: string) => add('success', message),
    error: (message: string) => add('error', message),
    warning: (message: string) => add('warning', message),
    info: (message: string) => add('info', message),
    dismiss,
    hold,
    release,
  }
}
