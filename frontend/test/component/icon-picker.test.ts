import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { defineComponent } from 'vue'
import { mount } from '@vue/test-utils'
import { describe, expect, test } from 'vitest'
import IconPicker from '../../src/components/ui/IconPicker.vue'
import { pickerIconNames } from '../../src/components/ui/icon-registry'

const ModalStub = defineComponent({
  props: { open: Boolean },
  template: '<div v-if="open"><slot /></div>',
})
const iconPickerSource = readFileSync(
  resolve(process.cwd(), 'src/components/ui/IconPicker.vue'),
  'utf8',
)

// 驗證 IconPicker 使用 registry 名稱並保留搜尋、選擇與 emit 行為。
describe('IconPicker', () => {
  // 防止元件重新維護另一份會與 registry 漂移的 icon array。
  test('uses the registry as its only ordered icon source', () => {
    expect(iconPickerSource).toMatch(/import\s*\{\s*pickerIconNames\s*\}\s*from\s*['"]\.\/icon-registry['"]/
    )
    expect(iconPickerSource).not.toMatch(/const\s+icons\s*=\s*\[/)
  })

  // 驗證搜尋會在 registry 提供的完整選項中保留相容的過濾行為。
  test('filters the ordered registry choices by search text', async () => {
    const wrapper = mount(IconPicker, {
      global: { stubs: { Modal: ModalStub } },
    })

    await wrapper.find('div.cursor-pointer').trigger('click')
    expect(wrapper.findAll('button')).toHaveLength(pickerIconNames.length)

    await wrapper.get('input').setValue('wallet')
    expect(wrapper.findAll('button')).toHaveLength(1)
    expect(wrapper.find('button span').text()).toBe('Wallet')
  })

  // 驗證選擇圖示仍會送出原本的 persisted PascalCase 名稱。
  test('emits the selected persisted icon name', async () => {
    const wrapper = mount(IconPicker, {
      global: { stubs: { Modal: ModalStub } },
    })

    await wrapper.find('div.cursor-pointer').trigger('click')
    await wrapper.get('input').setValue('wallet')
    await wrapper.find('button').trigger('click')

    expect(wrapper.emitted('update:modelValue')).toEqual([['Wallet']])
  })
})
