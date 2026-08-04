import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import { nextTick, ref } from 'vue'
import Input from '../../src/components/ui/Input.vue'
import Select from '../../src/components/ui/Select.vue'
import Modal from '../../src/components/ui/Modal.vue'
import ToastContainer from '../../src/components/ui/ToastContainer.vue'

describe('shared form control accessibility contracts', () => {
  it('associates Input identity and error state with its native control', async () => {
    const wrapper = mount(Input, {
      props: {
        modelValue: '',
        id: 'transaction-amount',
        error: '金額必須大於零',
      },
    })

    const input = wrapper.get('input')
    expect(input.attributes('id')).toBe('transaction-amount')
    expect(input.attributes('aria-invalid')).toBe('true')
    expect(input.attributes('aria-describedby')).toBe('transaction-amount-error')
    expect(wrapper.get('#transaction-amount-error').text()).toBe('金額必須大於零')

    await input.trigger('blur')
    expect(wrapper.emitted('blur')).toHaveLength(1)
  })

  it('associates Select identity and error state with its native control', async () => {
    const blur = vi.fn()
    const wrapper = mount(Select, {
      props: {
        modelValue: '',
        id: 'transaction-category',
        options: [{ value: 1, label: '餐飲' }],
        error: '請選擇類別',
        onBlur: blur,
      },
    })

    const select = wrapper.get('select')
    expect(select.attributes('id')).toBe('transaction-category')
    expect(select.attributes('aria-invalid')).toBe('true')
    expect(select.attributes('aria-describedby')).toBe('transaction-category-error')
    expect(wrapper.get('#transaction-category-error').text()).toBe('請選擇類別')

    await select.trigger('blur')
    expect(blur).toHaveBeenCalledTimes(1)
  })

  it('prevents a submitting modal from being closed by its close control', async () => {
    const wrapper = mount(Modal, {
      props: {
        open: true,
        title: '新增交易',
        closeDisabled: true,
      },
      slots: { default: '<p>表單內容</p>' },
      attachTo: document.body,
    })

    await nextTick()
    const close = document.body.querySelector('button[aria-label="關閉"]')
    expect(close).not.toBeNull()
    expect(close?.hasAttribute('disabled')).toBe(true)
    close?.click()
    expect(wrapper.emitted('update:open')).toBeUndefined()
    wrapper.unmount()
  })

  it('announces toast messages and exposes a keyboard dismiss action', async () => {
    const dismiss = vi.fn()
    const wrapper = mount(ToastContainer, {
      global: {
        provide: {
          toast: {
            toasts: ref([{ id: 1, type: 'success', message: '交易已建立' }]),
            dismiss,
          },
        },
      },
    })

    expect(wrapper.get('[role="status"]').attributes('aria-live')).toBe('polite')
    expect(wrapper.get('[role="status"]').text()).toContain('交易已建立')
    const close = wrapper.get('button[aria-label="關閉通知"]')
    await close.trigger('click')
    expect(dismiss).toHaveBeenCalledWith(1)
  })
})
