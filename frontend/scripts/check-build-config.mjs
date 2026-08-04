import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

// 建立可同時攔截 identifier、quoted 與 computed property 的設定比對式。
function propertyPattern(name) {
  return new RegExp(`(?:['"]?${name}['"]?|\\[\\s*['"]${name}['"]\\s*\\])\\s*:`)
}

const forbiddenSettings = [
  { name: 'chunkSizeWarningLimit', pattern: propertyPattern('chunkSizeWarningLimit') },
  { name: 'onwarn', pattern: propertyPattern('onwarn') },
  { name: 'manualChunks', pattern: propertyPattern('manualChunks') },
]

// 驗證 Vite config 沒有提高 warning threshold、抑制 warning 或搬移完整 namespace。
export function assertBuildConfig(source) {
  for (const setting of forbiddenSettings) {
    if (setting.pattern.test(source)) {
      throw new Error(`Forbidden bundle workaround in Vite config: ${setting.name}`)
    }
  }
}

// 讀取目前專案的 Vite config 並執行 production bundle 設定 guard。
export function runBuildConfigCheck(configPath = resolve(process.cwd(), 'vite.config.ts')) {
  assertBuildConfig(readFileSync(configPath, 'utf8'))
  console.log(`Bundle config guard passed: ${configPath}`)
}

const invokedScript = process.argv[1] ? resolve(process.argv[1]) : ''
if (invokedScript === fileURLToPath(import.meta.url)) {
  try {
    runBuildConfigCheck()
  } catch (error) {
    console.error(error instanceof Error ? error.message : error)
    process.exitCode = 1
  }
}
