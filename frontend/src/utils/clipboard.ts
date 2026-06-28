export interface ClipboardCopyEnvironment {
  clipboard?: Pick<Clipboard, 'writeText'> | null
  document?: Pick<Document, 'body' | 'createElement' | 'execCommand'> | null
}

/**
 * Copies text to the user's clipboard with a DOM fallback for browsers that hide Clipboard API.
 */
export async function copyTextToClipboard(
  text: string,
  environment: ClipboardCopyEnvironment = {},
): Promise<boolean> {
  const clipboard = environment.clipboard ?? globalThis.navigator?.clipboard

  if (clipboard?.writeText) {
    try {
      await clipboard.writeText(text)
      return true
    } catch {
      // Fall back to the legacy DOM copy path below.
    }
  }

  return copyTextWithDocument(text, environment.document ?? globalThis.document)
}

/**
 * Copies text through a temporary textarea and the legacy copy command.
 */
function copyTextWithDocument(
  text: string,
  documentRef: ClipboardCopyEnvironment['document'],
): boolean {
  if (!documentRef?.body || typeof documentRef.execCommand !== 'function') {
    return false
  }

  const textarea = documentRef.createElement('textarea')
  textarea.value = text
  textarea.setAttribute('readonly', '')
  textarea.style.position = 'fixed'
  textarea.style.left = '-9999px'
  textarea.style.opacity = '0'

  documentRef.body.appendChild(textarea)
  textarea.select()
  textarea.setSelectionRange(0, text.length)

  try {
    return documentRef.execCommand('copy')
  } finally {
    documentRef.body.removeChild(textarea)
  }
}
