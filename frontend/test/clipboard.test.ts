import assert from 'node:assert/strict'
import { test } from 'node:test'
import { copyTextToClipboard } from '../src/utils/clipboard.ts'

// Verifies the primary browser Clipboard API path still writes the full token.
test('copyTextToClipboard uses Clipboard API when it is available', async () => {
  let copiedText = ''
  const clipboard = {
    // Records the text sent to the browser Clipboard API.
    async writeText(text: string) {
      copiedText = text
    },
  }

  const copied = await copyTextToClipboard('secret-token', { clipboard })

  assert.equal(copied, true)
  assert.equal(copiedText, 'secret-token')
})

// Verifies fallback copy behavior for non-secure origins where navigator.clipboard is absent.
test('copyTextToClipboard uses document fallback when Clipboard API is unavailable', async () => {
  const createdElements: Array<Record<string, unknown>> = []
  const body = {
    appended: [] as Array<Record<string, unknown>>,
    removed: [] as Array<Record<string, unknown>>,
    // Records that the fallback textarea was attached for selection.
    appendChild(element: Record<string, unknown>) {
      this.appended.push(element)
    },
    // Records that the fallback textarea was removed after copying.
    removeChild(element: Record<string, unknown>) {
      this.removed.push(element)
    },
  }
  const document = {
    body,
    // Creates a textarea-like object that captures selection state.
    createElement(tagName: string) {
      const element = {
        tagName,
        value: '',
        style: {} as Record<string, string>,
        attributes: {} as Record<string, string>,
        selected: false,
        selection: null as [number, number] | null,
        // Records attributes applied by the fallback implementation.
        setAttribute(name: string, value: string) {
          this.attributes[name] = value
        },
        // Records that the fallback textarea was selected before copy.
        select() {
          this.selected = true
        },
        // Records the selected token range.
        setSelectionRange(start: number, end: number) {
          this.selection = [start, end]
        },
      }
      createdElements.push(element)
      return element
    },
    // Simulates the legacy copy command succeeding.
    execCommand(command: string) {
      return command === 'copy'
    },
  }

  const copied = await copyTextToClipboard('secret-token', { clipboard: undefined, document })

  assert.equal(copied, true)
  assert.equal(createdElements[0].value, 'secret-token')
  assert.equal(createdElements[0].selected, true)
  assert.deepEqual(createdElements[0].selection, [0, 12])
  assert.deepEqual(body.appended, createdElements)
  assert.deepEqual(body.removed, createdElements)
})
