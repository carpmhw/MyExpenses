import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { tmpdir } from 'node:os'
import { afterEach, describe, expect, test } from 'vitest'
import { checkEntrySize, runEntrySizeCheck } from '../../scripts/check-entry-size.mjs'

const fixtureRoots: string[] = []
const frontendPackage = JSON.parse(
  readFileSync(resolve(process.cwd(), 'package.json'), 'utf8'),
) as { scripts: { build: string } }
const viteConfigSource = readFileSync(resolve(process.cwd(), 'vite.config.ts'), 'utf8')

// 建立可重複使用的 Vite manifest 與 emitted chunk fixture。
function createFixture(entryBytes: number, includeRouteChunk = false): string {
  const root = mkdtempSync(join(tmpdir(), 'myexpenses-entry-size-'))
  fixtureRoots.push(root)
  mkdirSync(join(root, 'assets'), { recursive: true })
  writeFileSync(join(root, 'assets/main.js'), Buffer.alloc(entryBytes))

  const manifest: Record<string, unknown> = {
    'src/main.ts': {
      file: 'assets/main.js',
      isEntry: true,
      src: 'src/main.ts',
    },
  }
  if (includeRouteChunk) {
    writeFileSync(join(root, 'assets/dashboard.js'), Buffer.alloc(64))
    ;(manifest['src/main.ts'] as Record<string, unknown>).dynamicImports = [
      'src/pages/dashboard/index.vue',
    ]
    manifest['src/pages/dashboard/index.vue'] = {
      file: 'assets/dashboard.js',
      isDynamicEntry: true,
      src: 'src/pages/dashboard/index.vue',
    }
  }
  writeFileSync(join(root, 'manifest.json'), JSON.stringify(manifest))
  return root
}

// 移除測試產生的暫存 build fixture，避免污染工作區或系統暫存目錄。
afterEach(() => {
  for (const root of fixtureRoots.splice(0)) {
    rmSync(root, { recursive: true, force: true })
  }
})

// 驗證 manifest-based entry budget checker 的邊界與 lazy route 行為。
describe('entry size checker', () => {
  // 確保 production build 會執行 config guard 與 manifest entry budget checker。
  test('integrates the config and entry-size guards into production build', () => {
    expect(frontendPackage.scripts.build).toContain('node scripts/check-build-config.mjs')
    expect(frontendPackage.scripts.build).toContain('node scripts/check-entry-size.mjs')
    expect(viteConfigSource).toContain('manifest: true')
    expect(viteConfigSource).not.toMatch(/chunkSizeWarningLimit\s*:/)
    expect(viteConfigSource).not.toMatch(/manualChunks\s*:/)
    expect(viteConfigSource).not.toMatch(/onwarn\s*:/)
  })

  // 驗證 entry 在 budget 內時回傳 raw/gzip size 與 route metadata。
  test('accepts an entry within budget', () => {
    const distDir = createFixture(128, true)
    const result = checkEntrySize({
      distDir,
      manifestPath: join(distDir, 'manifest.json'),
    })

    expect(result.rawBytes).toBe(128)
    expect(result.budget).toBe(500000)
    expect(result.dynamicEntries.map((entry) => entry.file)).toEqual(['assets/dashboard.js'])
    expect(result.gzipBytes).toBeLessThanOrEqual(result.rawBytes)
  })

  // 驗證 Vite 8 以 index.html manifest key 代表 src/main.ts eager entry 的格式。
  test('resolves the Vite HTML entry for the main source', () => {
    const distDir = createFixture(128)
    const manifest = JSON.parse(readFileSync(join(distDir, 'manifest.json'), 'utf8')) as Record<string, unknown>
    delete manifest['src/main.ts']
    manifest['index.html'] = {
      file: 'assets/main.js',
      isEntry: true,
      src: 'index.html',
    }
    writeFileSync(join(distDir, 'manifest.json'), JSON.stringify(manifest))

    const result = checkEntrySize({
      distDir,
      manifestPath: join(distDir, 'manifest.json'),
    })

    expect(result.file).toBe('assets/main.js')
  })

  // 不把沒有 isEntry 標記的同名 chunk 誤認成 eager application entry。
  test('rejects a non-entry record for the main source', () => {
    const distDir = createFixture(128)
    const manifest = JSON.parse(readFileSync(join(distDir, 'manifest.json'), 'utf8')) as Record<string, Record<string, unknown>>
    manifest['src/main.ts'].isEntry = false
    writeFileSync(join(distDir, 'manifest.json'), JSON.stringify(manifest))

    expect(() => checkEntrySize({
      distDir,
      manifestPath: join(distDir, 'manifest.json'),
    })).toThrow(/src\/main\.ts/)
  })

  // 確保 production checker 驗證 main entry 確實引用了 lazy route chunks。
  test('requires the eager entry to reference its dynamic route chunks', () => {
    const distDir = createFixture(128, true)
    const manifest = JSON.parse(readFileSync(join(distDir, 'manifest.json'), 'utf8')) as Record<string, Record<string, unknown>>
    manifest['src/main.ts'].dynamicImports = []
    writeFileSync(join(distDir, 'manifest.json'), JSON.stringify(manifest))

    expect(() => runEntrySizeCheck({
      distDir,
      manifestPath: join(distDir, 'manifest.json'),
    })).toThrow(/dynamic route/)
  })

  // 驗證所有 route reference 與 emitted dynamic chunk 都存在時 production check 會通過。
  test('accepts referenced dynamic route chunks', () => {
    const distDir = createFixture(128, true)

    expect(() => runEntrySizeCheck({
      distDir,
      manifestPath: join(distDir, 'manifest.json'),
    })).not.toThrow()
  })

  // 驗證超過 budget 時錯誤會包含實際大小與設定上限。
  test('rejects an entry over budget with measured size', () => {
    const distDir = createFixture(500001)

    expect(() => checkEntrySize({
      distDir,
      manifestPath: join(distDir, 'manifest.json'),
    })).toThrow(/500001.*500000|500000.*500001/)
  })

  // 驗證找不到 src/main.ts entry 時不會誤判其他 chunk 為 eager entry。
  test('rejects a manifest without the application entry', () => {
    const distDir = createFixture(128)
    const manifest = JSON.parse(readFileSync(join(distDir, 'manifest.json'), 'utf8')) as Record<string, unknown>
    delete manifest['src/main.ts']
    writeFileSync(join(distDir, 'manifest.json'), JSON.stringify(manifest))

    expect(() => checkEntrySize({
      distDir,
      manifestPath: join(distDir, 'manifest.json'),
    })).toThrow(/src\/main\.ts/)
  })

  // 驗證 lazy route chunk 存在時只計算 eager main entry，不會合併計算。
  test('preserves route-level dynamic chunks while checking the eager entry', () => {
    const distDir = createFixture(256, true)
    const result = checkEntrySize({
      distDir,
      manifestPath: join(distDir, 'manifest.json'),
      budget: 256,
    })

    expect(result.rawBytes).toBe(256)
    expect(result.dynamicEntries).toHaveLength(1)
  })
})
