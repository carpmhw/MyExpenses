import { existsSync, readFileSync, statSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { gzipSync } from 'node:zlib'

const DEFAULT_ENTRY_SOURCE = 'src/main.ts'
const DEFAULT_ENTRY_BUDGET = 500_000

// 讀取並驗證 Vite 產生的 manifest JSON。
function readManifest(manifestPath) {
  if (!existsSync(manifestPath)) {
    throw new Error(`Vite manifest not found: ${manifestPath}`)
  }

  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'))
  if (!manifest || typeof manifest !== 'object' || Array.isArray(manifest)) {
    throw new Error(`Vite manifest must be an object: ${manifestPath}`)
  }
  return manifest
}

// 從 manifest 找到 src/main.ts 對應的 eager entry。
function findEntry(manifest, entrySource) {
  const directEntry = manifest[entrySource]
  if (directEntry && directEntry.isEntry === true && typeof directEntry.file === 'string') return directEntry

  const matchingEntry = Object.values(manifest).find((entry) => (
    entry && typeof entry === 'object' && entry.src === entrySource
      && entry.isEntry === true && typeof entry.file === 'string'
  ))
  if (matchingEntry) return matchingEntry

  if (entrySource === DEFAULT_ENTRY_SOURCE) {
    const htmlEntry = manifest['index.html']
    if (htmlEntry && htmlEntry.isEntry === true && typeof htmlEntry.file === 'string') {
      return htmlEntry
    }
  }

  throw new Error(`Vite manifest entry not found for ${entrySource}`)
}

// 收集 manifest 中的 dynamic route chunks，避免將其併入 eager entry 大小。
function collectDynamicEntries(manifest) {
  return Object.entries(manifest)
    .filter(([, entry]) => (
      entry && typeof entry === 'object' && entry.isDynamicEntry === true && typeof entry.file === 'string'
    ))
    .map(([source, entry]) => ({
      source,
      file: entry.file,
    }))
}

// 確認 eager entry 的 dynamicImports 都對應到可輸出的 lazy route chunk。
function validateDynamicRouteEntries({ distDir, dynamicImports, dynamicEntries }) {
  if (!Array.isArray(dynamicImports) || dynamicImports.length === 0) {
    throw new Error('No dynamic route imports found in the Vite manifest')
  }

  const dynamicEntryBySource = new Map(dynamicEntries.map((entry) => [entry.source, entry]))
  const missingEntries = dynamicImports.filter((source) => {
    const entry = dynamicEntryBySource.get(source)
    return !entry || !existsSync(resolve(distDir, entry.file))
  })
  if (missingEntries.length > 0) {
    throw new Error(`Missing dynamic route chunks: ${missingEntries.join(', ')}`)
  }
}

// 檢查 eager entry 的 raw/gzip 大小，並回傳可供 build log 與測試使用的量測結果。
export function checkEntrySize({
  distDir,
  manifestPath = resolve(distDir, '.vite/manifest.json'),
  entrySource = DEFAULT_ENTRY_SOURCE,
  budget = DEFAULT_ENTRY_BUDGET,
}) {
  if (!Number.isInteger(budget) || budget < 0) {
    throw new Error(`Entry budget must be a non-negative integer: ${budget}`)
  }

  const manifest = readManifest(manifestPath)
  const entry = findEntry(manifest, entrySource)
  const entryPath = resolve(distDir, entry.file)
  if (!existsSync(entryPath)) {
    throw new Error(`Emitted entry file not found: ${entry.file}`)
  }

  const rawBytes = statSync(entryPath).size
  const gzipBytes = gzipSync(readFileSync(entryPath)).byteLength
  const result = {
    distDir,
    entrySource,
    file: entry.file,
    rawBytes,
    gzipBytes,
    budget,
    dynamicImports: Array.isArray(entry.dynamicImports) ? entry.dynamicImports : [],
    dynamicEntries: collectDynamicEntries(manifest),
  }

  if (rawBytes > budget) {
    throw new Error(
      `Entry ${entrySource} is ${rawBytes} bytes, exceeding the ${budget}-byte budget`,
    )
  }

  return result
}

// 執行 production build 後的完整檢查，並確認 route-level lazy chunks 仍存在。
export function runEntrySizeCheck({
  distDir = resolve(process.cwd(), 'dist'),
  manifestPath = resolve(distDir, '.vite/manifest.json'),
  entrySource = DEFAULT_ENTRY_SOURCE,
  budget = DEFAULT_ENTRY_BUDGET,
} = {}) {
  const result = checkEntrySize({ distDir, manifestPath, entrySource, budget })
  validateDynamicRouteEntries(result)

  console.log(
    `Entry ${result.entrySource}: ${result.rawBytes} bytes raw, ${result.gzipBytes} bytes gzip (budget ${result.budget})`,
  )
  console.log(`Dynamic route chunks: ${result.dynamicEntries.length}`)
  return result
}

const invokedScript = process.argv[1] ? resolve(process.argv[1]) : ''
if (invokedScript === fileURLToPath(import.meta.url)) {
  const distDir = resolve(process.cwd(), process.argv[2] ?? 'dist')
  const budget = Number(process.argv[3] ?? DEFAULT_ENTRY_BUDGET)

  try {
    runEntrySizeCheck({ distDir, budget })
  } catch (error) {
    console.error(error instanceof Error ? error.message : error)
    process.exitCode = 1
  }
}
