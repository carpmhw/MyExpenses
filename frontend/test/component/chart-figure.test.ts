import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import { defineComponent, h } from 'vue'
import ChartFigure from '../../src/components/ui/ChartFigure.vue'

/** 模擬 vue-chartjs 圖表：只把收到的 a11y props 渲染成 canvas 屬性。 */
const FakeChart = defineComponent({
  props: { ariaLabel: String },
  setup(props) {
    return () => h('canvas', { role: 'img', 'aria-label': props.ariaLabel })
  },
})

/** 以指定資料掛載 ChartFigure，並把 slot 提供的 a11y props 傳給模擬圖表。 */
function mountFigure(props: Record<string, unknown>) {
  return mount(ChartFigure, {
    props: { label: '每月收支趨勢', ...props },
    slots: { default: (slotProps: { chartA11y: Record<string, unknown> }) => h(FakeChart, slotProps.chartA11y) },
  })
}

describe('ChartFigure 圖表文字替代', () => {
  /** canvas 取得包含圖表名稱的 aria-label，避免成為無名稱的圖片。 */
  it('names the chart canvas', () => {
    const wrapper = mountFigure({ data: { labels: ['2026-01'], datasets: [{ label: '收入', data: [100] }] } })

    expect(wrapper.get('canvas').attributes('aria-label')).toBe('每月收支趨勢，詳細數值見下方資料表')
  })

  /** 資料表以 caption 命名，欄列標題具 scope，數值使用與圖表相同的格式。 */
  it('renders the chart data as a captioned table with scoped headers', () => {
    const wrapper = mountFigure({
      data: {
        labels: ['2026-01', '2026-02'],
        datasets: [{ label: '收入', data: [1000, 2500] }, { label: '支出', data: [300, 1200] }],
      },
    })

    expect(wrapper.get('caption').text()).toBe('每月收支趨勢')
    expect(wrapper.findAll('thead th').map(th => [th.text(), th.attributes('scope')])).toEqual([
      ['月份', 'col'], ['收入', 'col'], ['支出', 'col'],
    ])
    const rows = wrapper.findAll('tbody tr').map(tr => tr.findAll('th, td').map(cell => cell.text()))
    expect(rows).toEqual([
      ['2026-01', 'NT$ 1,000', 'NT$ 300'],
      ['2026-02', 'NT$ 2,500', 'NT$ 1,200'],
    ])
    expect(wrapper.get('tbody th').attributes('scope')).toBe('row')
  })

  /** 缺值顯示「—」而非 0；無名稱資料集使用 valueLabel；可自訂數值格式。 */
  it('shows gaps as em dash and supports custom labels and formats', () => {
    const wrapper = mountFigure({
      label: '每月累積 TWR',
      valueLabel: '累積 TWR',
      format: (value: number) => `${value.toFixed(2)}%`,
      data: { labels: ['2026-01', '2026-02'], datasets: [{ data: [1.5, null] }] },
    })

    expect(wrapper.findAll('thead th').map(th => th.text())).toEqual(['月份', '累積 TWR'])
    expect(wrapper.findAll('tbody td').map(td => td.text())).toEqual(['1.50%', '—'])
  })
})
