import { mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import ToastContainer from '../../src/components/ui/ToastContainer.vue'
import { useToast } from '../../src/composables/useToast'

/** 以真實 useToast 掛載通知容器，掛到 document 以驗證焦點。 */
function mountToasts() {
  const toast = useToast()
  const wrapper = mount(ToastContainer, {
    attachTo: document.body,
    global: { provide: { toast } },
  })
  return { toast, wrapper }
}

describe('ToastContainer 通知可存取性', () => {
  beforeEach(() => {
    vi.useFakeTimers()
  })

  afterEach(() => {
    vi.useRealTimers()
    delete (document as { ariaNotify?: unknown }).ariaNotify
  })

  /** 通知區與備援 live region 在第一則通知前就存在，螢幕閱讀器才會朗讀後續插入的訊息。 */
  it('keeps the notification region and live regions in the DOM before any toast', async () => {
    const { toast, wrapper } = mountToasts()

    expect(wrapper.find('section[aria-label="通知"]').exists()).toBe(true)
    const polite = wrapper.get('[data-toast-announcer="polite"]')
    const assertive = wrapper.get('[data-toast-announcer="assertive"]')
    expect(polite.attributes('aria-live')).toBe('polite')
    expect(assertive.attributes('aria-live')).toBe('assertive')

    toast.success('交易已建立')
    toast.error('儲存失敗')
    for (let i = 0; i < 5; i++) await nextTick()

    expect(polite.text()).toBe('交易已建立')
    expect(assertive.text()).toBe('儲存失敗')
    wrapper.unmount()
  })

  /** 支援 ariaNotify 時只朗讀訊息文字，錯誤使用高優先權，且不再渲染備援 live region。 */
  it('uses ariaNotify with only the message text when available', async () => {
    const ariaNotify = vi.fn()
    Object.assign(document, { ariaNotify })
    const { toast, wrapper } = mountToasts()

    toast.info('已同步')
    toast.error('同步失敗')
    await nextTick()

    expect(ariaNotify).toHaveBeenNthCalledWith(1, '已同步', { priority: 'normal' })
    expect(ariaNotify).toHaveBeenNthCalledWith(2, '同步失敗', { priority: 'high' })
    expect(wrapper.find('[data-toast-announcer]').exists()).toBe(false)
    wrapper.unmount()
  })

  /** 焦點在通知內時暫停自動關閉，離開後重新計時。 */
  it('pauses auto-dismiss while a toast has focus', async () => {
    const { toast, wrapper } = mountToasts()
    toast.success('交易已建立')
    await nextTick()

    wrapper.get<HTMLButtonElement>('button[data-toast-close]').element.focus()
    vi.advanceTimersByTime(10_000)
    expect(toast.toasts.value).toHaveLength(1)

    ;(document.activeElement as HTMLElement).blur()
    vi.advanceTimersByTime(4000)
    expect(toast.toasts.value).toHaveLength(0)
    wrapper.unmount()
  })

  /** 鍵盤關閉通知後，焦點移到下一則通知；全部關閉後回到進入通知前的元素。 */
  it('moves focus to the next toast, then back to the prior element', async () => {
    const trigger = document.createElement('button')
    document.body.append(trigger)
    const { toast, wrapper } = mountToasts()
    toast.success('第一則')
    toast.success('第二則')
    await nextTick()

    trigger.focus()
    const [first] = wrapper.findAll<HTMLButtonElement>('button[data-toast-close]')
    first.element.focus()
    await first.trigger('click')
    await nextTick()

    const remaining = wrapper.get<HTMLButtonElement>('button[data-toast-close]')
    expect(document.activeElement).toBe(remaining.element)

    await remaining.trigger('click')
    await nextTick()
    expect(toast.toasts.value).toHaveLength(0)
    expect(document.activeElement).toBe(trigger)
    wrapper.unmount()
    trigger.remove()
  })
})
