/**
 * Resolves a CSS theme variable in the browser and returns a supplied fallback elsewhere.
 */
export function getThemeColor(token: string, fallback: string): string {
  if (typeof document === 'undefined' || typeof getComputedStyle === 'undefined') {
    return fallback
  }

  const value = getComputedStyle(document.documentElement).getPropertyValue(token).trim()
  return value || fallback
}
