import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
import test from 'node:test'
import { getThemeColor } from '../src/utils/themeColor.ts'

const testDirectory = dirname(fileURLToPath(import.meta.url))
const sourceDirectory = join(testDirectory, '..', 'src')

const readSource = (relativePath: string): string =>
  readFileSync(join(sourceDirectory, relativePath), 'utf8')

// Converts a hex color into the relative luminance used by WCAG contrast calculations.
function relativeLuminance(hex: string): number {
  const channels = [0, 2, 4].map(index => parseInt(hex.slice(index + 1, index + 3), 16) / 255)
  const linear = channels.map(channel =>
    channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4,
  )
  return 0.2126 * linear[0] + 0.7152 * linear[1] + 0.0722 * linear[2]
}

// Calculates the WCAG contrast ratio for two six-digit hex colors.
function contrastRatio(foreground: string, background: string): number {
  const foregroundLuminance = relativeLuminance(foreground)
  const backgroundLuminance = relativeLuminance(background)
  const lighter = Math.max(foregroundLuminance, backgroundLuminance)
  const darker = Math.min(foregroundLuminance, backgroundLuminance)
  return (lighter + 0.05) / (darker + 0.05)
}

test('dark theme declares the approved Nord hierarchy and preserves light values', () => {
  const style = readSource('style.css')

  assert.match(style, /--color-bg-app:\s*#F6F7F9;/)
  assert.match(style, /--color-accent-primary:\s*#10B981;/)
  assert.match(style, /\.dark\s*\{[\s\S]*--color-bg-sidebar:\s*#242A34;/)
  assert.match(style, /--color-bg-app:\s*#2E3440;/)
  assert.match(style, /--color-bg-card:\s*#3B4252;/)
  assert.match(style, /--color-bg-raised:\s*#434C5E;/)
  assert.match(style, /--color-bg-active:\s*#4C566A;/)
  assert.match(style, /--color-text-primary:\s*#ECEFF4;/)
  assert.match(style, /--color-text-secondary:\s*#B8C0CC;/)
  assert.match(style, /--color-text-tertiary:\s*#A8B1BE;/)
  assert.match(style, /--color-text-disabled:\s*#939BA7;/)
  assert.match(style, /--color-accent-primary:\s*#88C0D0;/)
  assert.match(style, /--color-accent-primary-hover:\s*#9CCDD8;/)
  assert.match(style, /--color-text-on-accent:\s*#242A34;/)
  assert.match(style, /--color-border-default:\s*#4B5563;/)
  assert.match(style, /--color-border-subtle:\s*#434C5E;/)
  assert.match(style, /--color-border-strong:\s*#8794A8;/)
  assert.match(style, /--color-color-income:\s*#A3BE8C;/)
  assert.match(style, /--color-color-expense:\s*#BF616A;/)
  assert.match(style, /--color-color-credit:\s*#B48EAD;/)
  assert.match(style, /--color-color-info:\s*#81A1C1;/)
  assert.match(style, /--color-color-warning:\s*#EBCB8B;/)
})

test('dark theme text and controls meet contrast thresholds', () => {
  const style = readSource('style.css')

  assert.match(style, /--color-color-expense-text:\s*#E6A5AB;/)
  assert.match(style, /--color-color-credit-text:\s*#C5A5BF;/)
  assert.match(style, /--color-color-info-text:\s*#9DB9D2;/)

  assert.ok(contrastRatio('#ECEFF4', '#2E3440') >= 4.5)
  assert.ok(contrastRatio('#B8C0CC', '#3B4252') >= 4.5)
  assert.ok(contrastRatio('#A8B1BE', '#3B4252') >= 4.5)
  assert.ok(contrastRatio('#88C0D0', '#242A34') >= 4.5)
  assert.ok(contrastRatio('#E6A5AB', '#3B4252') >= 4.5)
  assert.ok(contrastRatio('#C5A5BF', '#3B4252') >= 4.5)
  assert.ok(contrastRatio('#9DB9D2', '#3B4252') >= 4.5)
  assert.ok(contrastRatio('#8794A8', '#3B4252') >= 3)
})

test('shared components use role-based dark surfaces and foregrounds', () => {
  const sidebar = readSource('layouts/Sidebar.vue')
  const button = readSource('components/ui/Button.vue')
  const input = readSource('components/ui/Input.vue')
  const select = readSource('components/ui/Select.vue')

  assert.match(sidebar, /bg-bg-sidebar-active/)
  assert.match(sidebar, /hover:bg-bg-sidebar-raised/)
  assert.doesNotMatch(sidebar, /bg-\[#1E293B\]/)
  assert.match(button, /text-text-on-accent/)
  assert.match(button, /hover:bg-bg-control-hover/)
  assert.match(button, /bg-color-expense/)
  assert.match(input, /border-border-strong/)
  assert.match(input, /text-color-expense-text/)
  assert.match(select, /border-border-strong/)
})

test('dashboard and reports use the shared semantic palette', () => {
  const dashboard = readSource('pages/dashboard/index.vue')
  const reports = readSource('pages/reports/index.vue')

  assert.match(dashboard, /bg-color-income-hero-icon-bg/)
  assert.match(dashboard, /bg-color-expense-hero-icon-bg/)
  assert.match(dashboard, /bg-color-credit-hero-icon-bg/)
  assert.doesNotMatch(dashboard, /#0F172A|#1E293B|#065F46/)
  assert.match(reports, /getThemeColor/)
  assert.match(reports, /--color-color-income/)
  assert.match(reports, /--color-color-expense/)
  assert.match(reports, /--color-color-credit/)
})

test('theme color resolver is safe outside a browser', () => {
  const utility = readSource('utils/themeColor.ts')

  assert.match(utility, /export function getThemeColor/)
  assert.match(utility, /typeof document === ['"]undefined['"]/
  )
  assert.equal(getThemeColor('--color-accent-primary', '#88C0D0'), '#88C0D0')
})

test('dark fixed-sidebar controls and error boundaries remain readable', () => {
  const style = readSource('style.css')
  const sidebar = readSource('layouts/Sidebar.vue')
  const mobileHeader = readSource('components/ui/MobileHeader.vue')
  const input = readSource('components/ui/Input.vue')
  const iconPicker = readSource('components/ui/IconPicker.vue')

  assert.match(style, /--color-bg-sidebar-raised:\s*rgb\(255 255 255 \/ 5%\);/)
  assert.match(style, /--color-bg-sidebar-active:\s*#1E293B;/)
  assert.match(style, /--color-bg-mobile-sidebar-raised:\s*rgb\(255 255 255 \/ 10%\);/)
  assert.match(style, /\.dark\s*\{[\s\S]*--color-bg-sidebar-raised:\s*#434C5E;/)
  assert.match(style, /--color-bg-sidebar-active:\s*#4C566A;/)
  assert.ok(contrastRatio('#FFFFFF', '#1E293B') >= 4.5)
  assert.ok(contrastRatio('#ECEFF4', '#4C566A') >= 4.5)
  assert.match(sidebar, /bg-bg-sidebar-active/)
  assert.match(sidebar, /hover:bg-bg-sidebar-raised/)
  assert.match(mobileHeader, /hover:bg-bg-mobile-sidebar-raised/)
  assert.match(input, /error \? 'border-color-expense-text/)
  assert.match(iconPicker, /error \? 'border-color-expense-text'/)
})

test('all built-in chart axes and expense marks use theme colors', () => {
  const reports = readSource('pages/reports/index.vue')
  const snapshots = readSource('pages/snapshots/index.vue')
  const expenses = readSource('pages/expenses/index.vue')

  assert.match(reports, /expenseChartColor|color-color-expense-chart/)
  assert.match(reports, /border: \{ color: chartColors\.value\.axis \}/)
  assert.match(snapshots, /x:\s*\{\s*ticks:\s*\{\s*color: chartColors\.value\.text \}/)
  assert.match(snapshots, /border: \{ color: chartColors\.value\.axis \}/)
  assert.match(expenses, /text-color-expense-text cursor-pointer/)
})

// Verifies that role-specific colors do not regress the existing light theme.
test('light action colors and focused controls preserve their visual roles', () => {
  const style = readSource('style.css')
  const dashboard = readSource('pages/dashboard/index.vue')
  const toast = readSource('components/ui/ToastContainer.vue')
  const input = readSource('components/ui/Input.vue')
  const iconPicker = readSource('components/ui/IconPicker.vue')
  const select = readSource('components/ui/Select.vue')
  const reports = readSource('pages/reports/index.vue')
  const expenses = readSource('pages/expenses/index.vue')

  assert.match(style, /--color-color-expense-action:\s*#EF4444;/)
  assert.match(style, /--color-color-expense-toast:\s*#E11D48;/)
  assert.match(style, /--color-color-expense-chart:\s*#EF4444;/)
  assert.match(style, /--color-color-income-hero-bg:/)
  assert.match(dashboard, /bg-color-income-hero-bg/)
  assert.match(toast, /bg-color-expense-toast/)
  assert.match(input, /focus:ring-focus-ring/)
  assert.match(iconPicker, /focus:ring-focus-ring/)
  assert.match(select, /focus:ring-focus-ring/)
  assert.match(reports, /bg-bg-active text-text-primary shadow-sm/)
  assert.match(expenses, /bg-bg-active text-text-primary shadow-sm/)
})

// Covers visual roles that need separate chart and status treatments.
test('charts and page status roles retain light-mode rendering intent', () => {
  const style = readSource('style.css')
  const sidebar = readSource('layouts/Sidebar.vue')
  const button = readSource('components/ui/Button.vue')
  const modal = readSource('components/ui/Modal.vue')
  const dashboard = readSource('pages/dashboard/index.vue')
  const reports = readSource('pages/reports/index.vue')
  const snapshots = readSource('pages/snapshots/index.vue')
  const installments = readSource('pages/installments/index.vue')
  const settings = readSource('pages/settings/index.vue')

  assert.match(style, /--color-border-strong:\s*#E2E8F0;/)
  assert.match(style, /--color-bg-control-hover:\s*#F3F4F6;/)
  assert.match(style, /--color-border-overlay:\s*transparent;/)
  assert.match(style, /--color-border-sidebar-divider:\s*rgb\(255 255 255 \/ 10%\);/)
  assert.match(style, /--color-bg-hero-divider:\s*#475569;/)
  assert.match(style, /--color-text-on-hero-muted:\s*#94A3B8;/)
  assert.match(style, /--color-color-expense-hero-fg:\s*#FCA5A5;/)
  assert.match(style, /--color-color-income-chart-bg:/)
  assert.match(style, /--color-color-info-chart-bg:/)
  assert.match(style, /--color-color-warning-chart-bg:/)
  assert.match(style, /--color-chart-grid:\s*#E2E8F0;/)
  assert.match(style, /--color-chart-axis:\s*#CBD5E1;/)
  assert.match(style, /\.dark\s*\{[\s\S]*--color-chart-grid:\s*#4B5563;/)
  assert.match(style, /--color-chart-axis:\s*#8794A8;/)
  assert.match(dashboard, /from-color-income-panel-start/)
  assert.match(dashboard, /from-color-expense-panel-start/)
  assert.match(dashboard, /from-color-credit-panel-start/)
  assert.match(dashboard, /bg-color-income-hero-icon-bg/)
  assert.match(dashboard, /text-color-income-hero-fg/)
  assert.match(dashboard, /text-color-expense-hero-fg/)
  assert.match(dashboard, /text-color-credit-hero-fg/)
  assert.match(dashboard, /text-text-on-hero-muted/)
  assert.match(dashboard, /bg-bg-hero-divider/)
  assert.match(sidebar, /border-border-sidebar-divider/)
  assert.match(button, /hover:bg-bg-control-hover/)
  assert.match(modal, /border-border-overlay/)
  assert.match(reports, /color-income-chart-bg/)
  assert.match(reports, /backgroundColor: chartColors\.value\.surface/)
  assert.match(reports, /axis: getThemeColor\('--color-chart-axis'/)
  assert.match(snapshots, /color-info-chart-bg/)
  assert.match(snapshots, /tooltip:/)
  assert.match(installments, /bg-color-credit flex items-center/)
  assert.match(settings, /hover:text-color-expense-text/)
})
