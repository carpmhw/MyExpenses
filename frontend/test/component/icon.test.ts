import { mount } from '@vue/test-utils'
import { describe, expect, test, vi } from 'vitest'
import Icon from '../../src/components/ui/Icon.vue'

// 驗證共用 Icon 元件的名稱解析、fallback 與既有視覺 API。
describe('Icon', () => {
  // 驗證 PascalCase 名稱仍能產生對應 SVG。
  test('renders a PascalCase icon name', () => {
    const wrapper = mount(Icon, { props: { name: 'Loader2' } })

    expect(wrapper.find('svg').exists()).toBe(true)
  })

  // 驗證 kebab-case 名稱與既有視覺 props 及 caller class 都能保留。
  test('normalizes kebab-case names and preserves visual props and classes', () => {
    const wrapper = mount(Icon, {
      props: {
        name: 'trash-2',
        size: 24,
        color: '#2563eb',
        strokeWidth: 1.5,
      },
      attrs: { class: 'custom-icon' },
    })
    const svg = wrapper.find('svg')

    expect(svg.attributes('width')).toBe('24')
    expect(svg.attributes('height')).toBe('24')
    expect(svg.attributes('stroke')).toBe('#2563eb')
    expect(svg.attributes('stroke-width')).toBe('1.5')
    expect(svg.classes()).toContain('custom-icon')
  })

  // 驗證未知或空白的 persisted icon value 使用中性 fallback 並持續渲染。
  test('renders a neutral fallback for unsupported names without throwing', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined)
    const wrapper = mount(Icon, { props: { name: 'not-a-real-icon' } })

    expect(wrapper.find('svg').exists()).toBe(true)
    expect(warn).toHaveBeenCalledWith(expect.stringContaining('NotARealIcon'))
  })
})
