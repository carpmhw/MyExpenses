import { ref } from 'vue'

export type ToastType = 'success' | 'error' | 'warning' | 'info'

export interface Toast {
  id: number
  type: ToastType
  message: string
}

let nextId = 1

export function useToast() {
  const toasts = ref<Toast[]>([])

  function add(type: ToastType, message: string) {
    const id = nextId++
    toasts.value.push({ id, type, message })
    setTimeout(() => dismiss(id), 4000)
  }

  function dismiss(id: number) {
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
  }
}
