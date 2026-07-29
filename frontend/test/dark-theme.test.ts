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

test('dark theme declares the approved Nord hierarchy', () => {
  const style = readSource('style.css')

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

test('light theme declares the approved Snow Storm hierarchy and semantic roles', () => {
  const style = readSource('style.css')

  assert.match(style, /--color-bg-app:\s*#E5E9F0;/)
  assert.match(style, /--color-bg-card:\s*#F8FAFC;/)
  assert.match(style, /--color-bg-sidebar:\s*#C8D2DE;/)
  assert.match(style, /--color-bg-sidebar-raised:\s*#D5DEE8;/)
  assert.match(style, /--color-bg-sidebar-active:\s*#ECEFF4;/)
  assert.match(style, /--color-bg-raised:\s*#ECEFF4;/)
  assert.match(style, /--color-bg-active:\s*#D8DEE9;/)
  assert.match(style, /--color-bg-control-hover:\s*#E5E9F0;/)
  assert.match(style, /--color-accent-primary:\s*#4D7D7D;/)
  assert.match(style, /--color-accent-primary-hover:\s*#416D6D;/)
  assert.match(style, /--color-focus-ring:\s*#4D7D7D;/)
  assert.match(style, /--color-text-primary:\s*#2E3440;/)
  assert.match(style, /--color-text-secondary:\s*#4C566A;/)
  assert.match(style, /--color-text-tertiary:\s*#5B6A7D;/)
  assert.match(style, /--color-text-disabled:\s*#8794A8;/)
  assert.match(style, /--color-text-on-dark:\s*#2E3440;/)
  assert.match(style, /--color-text-on-dark-muted:\s*#4C566A;/)
  assert.match(style, /--color-text-on-accent:\s*#FFFFFF;/)
  assert.match(style, /--color-border-default:\s*#D2DAE4;/)
  assert.match(style, /--color-border-subtle:\s*#E1E6EC;/)
  assert.match(style, /--color-border-strong:\s*#758399;/)
  assert.match(style, /--color-border-overlay:\s*#D2DAE4;/)
  assert.match(style, /--color-border-sidebar-divider:\s*#AEBCCB;/)
  assert.match(style, /--color-chart-grid:\s*#D2DAE4;/)
  assert.match(style, /--color-chart-axis:\s*#758399;/)
  assert.match(style, /--color-bg-hero-start:\s*#D8DEE9;/)
  assert.match(style, /--color-bg-hero-mid:\s*#C8D2DE;/)
  assert.match(style, /--color-bg-hero-end:\s*#B8D0CF;/)
  assert.match(style, /--color-color-income:\s*#5F7D50;/)
  assert.match(style, /--color-color-income-bg:\s*#E7F0E3;/)
  assert.match(style, /--color-color-income-text:\s*#4F7140;/)
  assert.match(style, /--color-color-income-chart:\s*#6F8F5E;/)
  assert.match(style, /--color-color-expense:\s*#AA4F5A;/)
  assert.match(style, /--color-color-expense-bg:\s*#F7E6E8;/)
  assert.match(style, /--color-color-expense-text:\s*#923D48;/)
  assert.match(style, /--color-color-expense-chart:\s*#AA4F5A;/)
  assert.match(style, /--color-color-credit:\s*#8D6A88;/)
  assert.match(style, /--color-color-credit-bg:\s*#EEE7ED;/)
  assert.match(style, /--color-color-credit-text:\s*#74546F;/)
  assert.match(style, /--color-color-info:\s*#4F759D;/)
  assert.match(style, /--color-color-info-bg:\s*#E5EDF5;/)
  assert.match(style, /--color-color-info-text:\s*#3E678E;/)
  assert.match(style, /--color-color-warning:\s*#95631F;/)
  assert.match(style, /--color-color-warning-bg:\s*#F7EEDA;/)
  assert.match(style, /--color-color-warning-text:\s*#82551C;/)
  assert.match(style, /--color-color-warning-action:\s*#EBCB8B;/)
  assert.match(style, /--color-color-warning-action-text:\s*#2E3440;/)
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

test('light theme text and controls meet contrast thresholds', () => {
  assert.ok(contrastRatio('#2E3440', '#E5E9F0') >= 4.5)
  assert.ok(contrastRatio('#4C566A', '#F8FAFC') >= 4.5)
  assert.ok(contrastRatio('#5B6A7D', '#E5E9F0') >= 4.5)
  assert.ok(contrastRatio('#5B6A7D', '#F8FAFC') >= 4.5)
  assert.ok(contrastRatio('#FFFFFF', '#4D7D7D') >= 4.5)
  assert.ok(contrastRatio('#FFFFFF', '#5F7D50') >= 4.5)
  assert.ok(contrastRatio('#FFFFFF', '#AA4F5A') >= 4.5)
  assert.ok(contrastRatio('#FFFFFF', '#8D6A88') >= 4.5)
  assert.ok(contrastRatio('#FFFFFF', '#4F759D') >= 4.5)
  assert.ok(contrastRatio('#FFFFFF', '#95631F') >= 4.5)
  assert.ok(contrastRatio('#2E3440', '#EBCB8B') >= 4.5)
  assert.ok(contrastRatio('#758399', '#E5E9F0') >= 3)
  assert.ok(contrastRatio('#758399', '#F8FAFC') >= 3)
  assert.ok(contrastRatio('#4D7D7D', '#E5E9F0') >= 3)
  assert.ok(contrastRatio('#2E3440', '#C8D2DE') >= 4.5)
  assert.ok(contrastRatio('#4C566A', '#C8D2DE') >= 4.5)
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

  assert.match(style, /--color-bg-sidebar-raised:\s*#D5DEE8;/)
  assert.match(style, /--color-bg-sidebar-active:\s*#ECEFF4;/)
  assert.match(style, /--color-bg-mobile-sidebar-raised:\s*#D5DEE8;/)
  assert.match(style, /\.dark\s*\{[\s\S]*--color-bg-sidebar-raised:\s*#434C5E;/)
  assert.match(style, /--color-bg-sidebar-active:\s*#4C566A;/)
  assert.ok(contrastRatio('#2E3440', '#C8D2DE') >= 4.5)
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

  assert.match(style, /--color-color-expense-action:\s*#AA4F5A;/)
  assert.match(style, /--color-color-expense-toast:\s*#AA4F5A;/)
  assert.match(style, /--color-color-expense-chart:\s*#AA4F5A;/)
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

  assert.match(style, /--color-border-strong:\s*#758399;/)
  assert.match(style, /--color-bg-control-hover:\s*#E5E9F0;/)
  assert.match(style, /--color-border-overlay:\s*#D2DAE4;/)
  assert.match(style, /--color-border-sidebar-divider:\s*#AEBCCB;/)
  assert.match(style, /--color-bg-hero-divider:\s*#96A6B8;/)
  assert.match(style, /--color-text-on-hero-muted:\s*#4C566A;/)
  assert.match(style, /--color-color-income-hero-fg:\s*#3B5D31;/)
  assert.match(style, /--color-color-expense-hero-fg:\s*#87313D;/)
  assert.match(style, /--color-color-credit-hero-fg:\s*#674A63;/)
  assert.match(style, /--color-color-income-chart-bg:/)
  assert.match(style, /--color-color-info-chart-bg:/)
  assert.match(style, /--color-color-warning-chart-bg:/)
  assert.match(style, /--color-chart-grid:\s*#D2DAE4;/)
  assert.match(style, /--color-chart-axis:\s*#758399;/)
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

test('light hero semantic foregrounds meet contrast across Frost gradient stops', () => {
  const heroBackgrounds = ['#D8DEE9', '#C8D2DE', '#B8D0CF']

  for (const background of heroBackgrounds) {
    assert.ok(contrastRatio('#3B5D31', background) >= 4.5)
    assert.ok(contrastRatio('#87313D', background) >= 4.5)
    assert.ok(contrastRatio('#674A63', background) >= 4.5)
  }
})

test('light chart fallbacks mirror refreshed theme tokens', () => {
  const reports = readSource('pages/reports/index.vue')
  const snapshots = readSource('pages/snapshots/index.vue')

  assert.match(reports, /theme === 'dark' \? '#B8C0CC' : '#4C566A'/)
  assert.match(reports, /theme === 'dark' \? '#8794A8' : '#758399'/)
  assert.match(reports, /theme === 'dark' \? '#A3BE8C' : '#6F8F5E'/)
  assert.match(reports, /theme === 'dark' \? '#BF616A' : '#AA4F5A'/)
  assert.match(snapshots, /theme === 'dark' \? '#3B4252' : '#F8FAFC'/)
  assert.match(snapshots, /theme === 'dark' \? '#81A1C1' : '#4F759D'/)
  assert.match(snapshots, /theme === 'dark' \? '#EBCB8B' : '#A56C26'/)
})

test('light color inputs use visible themed boundaries and focus indicators', () => {
  const categories = readSource('pages/categories/index.vue')
  const paymentMethods = readSource('pages/payment-methods/index.vue')

  assert.match(categories, /type="color"[\s\S]*border-border-strong[\s\S]*focus:ring-2 focus:ring-focus-ring/)
  assert.match(paymentMethods, /type="color"[\s\S]*border-border-strong[\s\S]*focus:ring-2 focus:ring-focus-ring/)
})
