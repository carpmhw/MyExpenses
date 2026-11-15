<script setup lang="ts">
import { computed, useId } from 'vue'
import { formatMoney } from '../../utils/format'

/** 與 Chart.js 共用的最小資料形狀：每個 label 對應各資料集同位置的數值，null 代表缺值。 */
interface ChartFigureData {
  labels?: unknown[]
  datasets: { label?: string; data: (number | null)[] }[]
}

const props = withDefaults(defineProps<{
  /** 圖表的可存取名稱，同時作為 canvas 的 aria-label 與資料表標題。 */
  label: string
  /** 與傳給圖表相同的資料，用來產生文字替代資料表。 */
  data: ChartFigureData
  /** 資料表第一欄（labels）的欄名。 */
  categoryLabel?: string
  /** 沒有名稱的資料集（例如圓餅圖）在資料表中的欄名。 */
  valueLabel?: string
  /** 數值格式；應與圖表 tooltip 一致。 */
  format?: (value: number) => string
}>(), {
  categoryLabel: '月份',
  valueLabel: '金額',
  format: (value: number) => formatMoney(value),
})

const tableId = useId()

/** 傳給 vue-chartjs 的 a11y props：canvas 已是 role="img"，補上名稱並指向資料表。 */
const chartA11y = computed(() => ({ ariaLabel: `${props.label}，詳細數值見下方資料表` }))

/** 資料表欄位：每個資料集一欄，沒有名稱時使用 valueLabel。 */
const columns = computed(() => props.data.datasets.map(dataset => dataset.label || props.valueLabel))

/** 資料表列：每個 label 一列，缺值顯示「—」而不是 0，避免誤導。 */
const rows = computed(() => (props.data.labels ?? []).map((label, index) => ({
  label: String(label),
  values: props.data.datasets.map(dataset => {
    const value = dataset.data[index]
    return value === null || value === undefined ? '—' : props.format(value)
  }),
})))
</script>

<template>
  <figure class="m-0">
    <slot :chart-a11y="chartA11y" />
    <details class="mt-3 text-xs text-text-secondary">
      <summary class="cursor-pointer w-fit rounded focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-focus-ring">
        檢視資料表
      </summary>
      <div role="region" tabindex="0" :aria-labelledby="tableId" class="mt-2 overflow-x-auto rounded-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-focus-ring">
        <table class="w-full text-xs">
          <caption :id="tableId" class="sr-only">{{ label }}</caption>
          <thead>
            <tr class="border-b border-border-default">
              <th scope="col" class="py-2 px-3 text-left font-medium">{{ categoryLabel }}</th>
              <th v-for="column in columns" :key="column" scope="col" class="py-2 px-3 text-right font-medium">{{ column }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(row, rowIndex) in rows" :key="`${rowIndex}-${row.label}`" class="border-b border-border-default">
              <th scope="row" class="py-2 px-3 text-left font-normal text-text-primary">{{ row.label }}</th>
              <td v-for="(value, index) in row.values" :key="index" class="py-2 px-3 text-right tabular-nums text-text-primary">{{ value }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </details>
  </figure>
</template>
