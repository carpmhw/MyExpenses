import { afterEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { api } from '../../src/api'
import SettingsPage from '../../src/pages/settings/index.vue'
import { mountWithAppProviders } from '../support/render'

/** 等待一個 macrotask，讓 API promise 與 Vue 重新渲染完成。 */
async function flushPromises(): Promise<void> {
  await new Promise(resolve => setTimeout(resolve, 0))
}

const token = {
  id: 7,
  name: 'MCP 桌面',
  prefix: 'me_abc',
  createdAt: '2026-08-01T00:00:00Z',
  lastUsedAt: null,
  isRevoked: false,
  scopes: null,
}

/** 掛載設定頁並載入一把可撤銷的 API 金鑰。 */
async function mountSettings() {
  vi.spyOn(api.apiTokens, 'list').mockResolvedValue([token] as never)
  const wrapper = mountWithAppProviders(SettingsPage, { attachTo: document.body })
  await flushPromises()
  return wrapper
}

/** 依按鈕文字在整份 document 中尋找按鈕（對話框 portal 到 body）。 */
function buttonByText(text: string): HTMLButtonElement {
  const button = Array.from(document.querySelectorAll('button')).find(b => b.textContent?.trim() === text)
  expect(button, `找不到按鈕：${text}`).toBeTruthy()
  return button as HTMLButtonElement
}

describe('設定頁撤銷 API 金鑰', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  /** 撤銷確認使用具名的 alertdialog，確認後才呼叫撤銷 API。 */
  it('confirms revocation in an accessible alert dialog', async () => {
    const revoke = vi.spyOn(api.apiTokens, 'revoke').mockResolvedValue(undefined)
    const wrapper = await mountSettings()

    buttonByText('撤銷').click()
    await nextTick()
    await nextTick()

    const dialog = document.querySelector('[role="alertdialog"]')
    expect(dialog).not.toBeNull()
    const title = document.getElementById(dialog!.getAttribute('aria-labelledby')!)
    expect(title?.textContent?.trim()).toBe('撤銷 API 金鑰')
    expect(dialog!.textContent).toContain('確定要撤銷「MCP 桌面」嗎？此操作無法復原。')
    expect(revoke).not.toHaveBeenCalled()

    buttonByText('確認撤銷').click()
    await flushPromises()

    expect(revoke).toHaveBeenCalledWith(7)
    wrapper.unmount()
  })

  /** 取消確認不會撤銷金鑰。 */
  it('does not revoke when the dialog is cancelled', async () => {
    const revoke = vi.spyOn(api.apiTokens, 'revoke').mockResolvedValue(undefined)
    const wrapper = await mountSettings()

    buttonByText('撤銷').click()
    await nextTick()
    await nextTick()
    buttonByText('取消').click()
    await flushPromises()

    expect(revoke).not.toHaveBeenCalled()
    expect(document.querySelector('[role="alertdialog"]')).toBeNull()
    wrapper.unmount()
  })
})
