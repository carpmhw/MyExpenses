import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, test } from 'vitest'

const iconSource = readFileSync(resolve(process.cwd(), 'src/components/ui/Icon.vue'), 'utf8')
const baseline = JSON.parse(
  readFileSync(resolve(process.cwd(), 'test/fixtures/entry-size-baseline.json'), 'utf8'),
) as { entry: string; rawBytes: number; gzipBytes: number }

// 驗證共用圖示元件的來源契約。
describe('icon source contract', () => {
  // 記錄最佳化前的 eager entry bundle 基準值。
  test('records the current eager entry baseline', () => {
    expect(baseline).toEqual({
      entry: 'src/main.ts',
      rawBytes: 754180,
      gzipBytes: 200350,
    })
  })

  // 拒絕會阻止 Lucide tree-shaking 的 wildcard 或 namespace import。
  test('rejects Lucide wildcard and namespace imports in the shared icon path', () => {
    expect(iconSource).not.toMatch(/import\s+\*\s+as\s+\w+\s+from\s+['"]@lucide\/vue['"]/
    )
  })
})
