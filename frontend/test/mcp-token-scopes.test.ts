import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import test from 'node:test'

const settings = readFileSync(new URL('../src/pages/settings/index.vue', import.meta.url), 'utf8')

// 驗證新 token 預設包含兩個唯讀 scope，但不預設授予刪除權限。
test('new MCP token defaults include reference reads but not transaction deletion', () => {
  const defaults = settings.match(/const mcpDefaultScopes = \[([\s\S]*?)\]/)?.[1]
  assert.ok(defaults)
  assert.match(defaults, /'agent-context:read'/)
  assert.match(defaults, /'credit-cards:read'/)
  assert.doesNotMatch(defaults, /'transactions:delete'/)

  const options = settings.match(/const apiTokenScopeOptions = \[([\s\S]*?)\]/)?.[1]
  assert.ok(options)
  assert.match(options, /value: 'agent-context:read'/)
  assert.match(options, /value: 'credit-cards:read'/)
})

// 驗證預設值只初始化新 token 表單，載入既有 token 不會合併或擴張 scopes。
test('existing tokens are loaded unchanged while defaults apply only to new forms', () => {
  assert.match(settings, /const newTokenScopes = ref<string\[\]>\(\[\.\.\.mcpDefaultScopes\]\)/)
  const fetchTokens = settings.match(/async function fetchTokens\(\) \{([\s\S]*?)\n\}/)?.[1]
  assert.ok(fetchTokens)
  assert.match(fetchTokens, /tokens\.value = await api\.apiTokens\.list\(\)/)
  assert.doesNotMatch(fetchTokens, /mcpDefaultScopes|\.scopes\s*=/)
  assert.match(settings, /api\.apiTokens\.create\(newTokenName\.value\.trim\(\), newTokenScopes\.value\)/)
})
