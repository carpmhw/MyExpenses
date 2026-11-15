import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../../src/api'
import LoginPage from '../../src/pages/login/index.vue'
import { mountWithAppProviders } from '../support/render'

/** 等待一個 macrotask，讓 auth 初始化的 promise 鏈與 Vue 重新渲染完成。 */
async function flushPromises(): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 0))
}

/** 以指定的 hasUsers 狀態掛載登入頁，並掛到 document 以便驗證焦點。 */
async function mountLogin(hasUsers: boolean) {
  vi.spyOn(api.auth, 'status').mockResolvedValue({ authenticated: false, user: null, hasUsers } as never)
  const wrapper = mountWithAppProviders(LoginPage, { attachTo: document.body })
  await flushPromises()
  return wrapper
}

/** 回傳 label 透過 for 屬性關聯到的 input。 */
function inputForLabel(text: string): HTMLInputElement {
  const label = Array.from(document.querySelectorAll('label')).find(l => l.textContent?.trim() === text)
  expect(label, `找不到 label：${text}`).toBeTruthy()
  const input = document.getElementById(label!.htmlFor)
  expect(input, `label「${text}」未關聯 input`).toBeInstanceOf(HTMLInputElement)
  return input as HTMLInputElement
}

describe('登入頁表單自動填入與可存取性', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    document.body.innerHTML = ''
  })

  /** 驗證登入表單使用 username／current-password，讓密碼管理器能正確填入。 */
  it('labels sign-in fields and uses sign-in autocomplete tokens', async () => {
    const wrapper = await mountLogin(true)

    expect(inputForLabel('Email').autocomplete).toBe('username')
    expect(inputForLabel('密碼').autocomplete).toBe('current-password')
    wrapper.unmount()
  })

  /** 驗證註冊表單的新密碼欄位使用 new-password，避免填入既有密碼。 */
  it('uses new-password for owner registration passwords', async () => {
    const wrapper = await mountLogin(false)

    expect(inputForLabel('Email').autocomplete).toBe('username')
    expect(inputForLabel('密碼').autocomplete).toBe('new-password')
    expect(inputForLabel('確認密碼').autocomplete).toBe('new-password')
    wrapper.unmount()
  })

  /** 驗證進入兩步驟驗證後聚焦 one-time-code 欄位，並以表單送出驗證碼。 */
  it('focuses the one-time-code input and submits it through the form', async () => {
    vi.spyOn(api.auth, 'login').mockResolvedValue({ requiresTwoFactor: true, tempToken: 'temp' } as never)
    const verify = vi.spyOn(api.auth, 'verify2fa').mockRejectedValue(new Error('invalid'))
    const wrapper = await mountLogin(true)

    await wrapper.get('#login-email').setValue('owner@example.com')
    await wrapper.get('#login-password').setValue('secret')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const code = wrapper.get('#verify-2fa-code').element as HTMLInputElement
    expect(code.autocomplete).toBe('one-time-code')
    expect(document.activeElement).toBe(code)

    await wrapper.get('#verify-2fa-code').setValue('123456')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(verify).toHaveBeenCalledWith({ tempToken: 'temp', code: '123456' })
    expect(wrapper.get('[role="alert"]').text()).toBe('驗證失敗')
    wrapper.unmount()
  })
})
