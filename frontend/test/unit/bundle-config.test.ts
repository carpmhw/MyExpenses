import { describe, expect, test } from 'vitest'
import { assertBuildConfig } from '../../scripts/check-build-config.mjs'

// 驗證 bundle 設定 guard 會阻止以設定掩蓋 oversized entry 的做法。
describe('bundle build config guard', () => {
  // 允許沒有規避設定的正常 Vite config。
  test('accepts an unmodified warning configuration', () => {
    expect(() => assertBuildConfig('export default { build: { manifest: true } }')).not.toThrow()
  })

  // 拒絕提高 Vite chunk warning threshold。
  test('rejects a raised chunkSizeWarningLimit', () => {
    expect(() => assertBuildConfig('build: { chunkSizeWarningLimit: 900 }')).toThrow(/chunkSizeWarningLimit/)
  })

  // 拒絕 warning suppression 或以 manualChunks 搬移完整 namespace 的 workaround。
  test('rejects warning suppression and manual chunk workarounds', () => {
    expect(() => assertBuildConfig('build: { rollupOptions: { onwarn: () => undefined } }')).toThrow(/onwarn/)
    expect(() => assertBuildConfig('build: { rollupOptions: { output: { manualChunks: {} } } }')).toThrow(/manualChunks/)
  })

  // 拒絕以 quoted 或 computed property name 繞過設定 guard。
  test('rejects quoted and computed workaround keys', () => {
    expect(() => assertBuildConfig("build: { 'chunkSizeWarningLimit': 900 }")).toThrow(/chunkSizeWarningLimit/)
    expect(() => assertBuildConfig("build: { ['manualChunks']: {} }")).toThrow(/manualChunks/)
  })
})
