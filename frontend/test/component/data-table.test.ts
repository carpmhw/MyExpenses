import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import DataTable from '../../src/components/ui/DataTable.vue'

const columns = [{ key: 'name', label: '名稱' }, { key: 'amount', label: '金額', align: 'right' as const }]

/** 以指定 props 掛載 DataTable，預設帶一列資料。 */
function mountTable(props: Record<string, unknown> = {}) {
  return mount(DataTable, {
    props: { caption: '交易明細', columns, items: [{ id: 1 }], ...props },
    slots: { default: '<tr><td>午餐</td><td>120</td><td></td></tr>' },
  })
}

describe('DataTable 可存取性', () => {
  /** 可捲動區域可聚焦，並以表格 caption 命名。 */
  it('names a keyboard-focusable scroll region with the table caption', () => {
    const wrapper = mountTable()

    const region = wrapper.get('[role="region"]')
    expect(region.attributes('tabindex')).toBe('0')
    const caption = wrapper.get('caption')
    expect(caption.text()).toBe('交易明細')
    expect(region.attributes('aria-labelledby')).toBe(caption.attributes('id'))
  })

  /** 欄位標題宣告 scope="col"，讓螢幕閱讀器把儲存格對應到欄名。 */
  it('scopes every column header', () => {
    const wrapper = mountTable()

    const headers = wrapper.findAll('thead th')
    expect(headers.map(h => h.text())).toEqual(['名稱', '金額', '操作'])
    expect(headers.every(h => h.attributes('scope') === 'col')).toBe(true)
  })

  /** 重新整理狀態區常駐 DOM，只切換內容，並標示表格忙碌中。 */
  it('keeps the refresh status region mounted and toggles its text', async () => {
    const wrapper = mountTable()
    const status = wrapper.get('[role="status"]')
    expect(status.text()).toBe('')
    expect(wrapper.get('table').attributes('aria-busy')).toBeUndefined()

    await wrapper.setProps({ refreshing: true })

    expect(wrapper.get('[role="status"]').element).toBe(status.element)
    expect(status.text()).toBe('重新整理中...')
    expect(wrapper.get('table').attributes('aria-busy')).toBe('true')
  })
})
