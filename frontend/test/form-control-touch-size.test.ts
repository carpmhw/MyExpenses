import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const style = readFileSync(new URL('../src/style.css', import.meta.url), 'utf8')

// 驗證觸控裝置上的文字類表單控制項至少 16px，避免 iOS Safari 聚焦時自動縮放頁面。
test('touch devices raise small form-control text to at least 16px', () => {
  const coarseBlocks = [...style.matchAll(/@media \(pointer: coarse\) \{([\s\S]*?)\n\s*\}\n\s*\}/g)].map(match => match[1])
  const controlSelector = ':is(input:not([type="checkbox"], [type="radio"], [type="range"], [type="color"]), select, textarea)'

  assert.ok(coarseBlocks.some(block => block.includes(controlSelector) && /font-size:\s*max\(1rem,\s*1em\);/.test(block)),
    '沒有字級 utility 的控制項應在 base 層至少 1rem')
  assert.ok(coarseBlocks.some(block => block.includes(controlSelector)
    && /--text-xs:\s*1rem;/.test(block)
    && /--text-sm:\s*1rem;/.test(block)),
    'text-xs／text-sm utility 在觸控控制項上應解析為 1rem')
  assert.match(style, /@layer base \{\s*@media \(pointer: coarse\)/, '無 utility 的規則必須在 base 層，讓 utility 可以覆寫')
})
