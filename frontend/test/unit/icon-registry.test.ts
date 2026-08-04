import { readdirSync, readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, expect, test } from 'vitest'
import {
  iconRegistry,
  normalizeIconName,
  pickerIconNames,
  resolveIcon,
} from '../../src/components/ui/icon-registry'

const repoRoot = resolve(process.cwd(), '..')
const frontendSourceRoot = resolve(process.cwd(), 'src')
const registrySource = readFileSync(
  resolve(process.cwd(), 'src/components/ui/icon-registry.ts'),
  'utf8',
)
const expectedPickerIconNames = [
  'Wallet', 'Banknote', 'CreditCard', 'Building2', 'DollarSign', 'Percent',
  'TrendingUp', 'TrendingDown', 'BarChart3', 'PieChart', 'Activity', 'Target',
  'Utensils', 'Coffee', 'Pizza', 'Apple', 'Cake',
  'Car', 'Train', 'Bus', 'Bike', 'Plane', 'Navigation',
  'Home', 'ShoppingCart', 'BaggageClaim', 'Gift', 'Package',
  'Smartphone', 'Laptop', 'Tv', 'Gamepad2', 'Music', 'Headphones', 'Camera', 'Film',
  'BookOpen', 'GraduationCap', 'Pen', 'FileText',
  'HeartPulse', 'Pill', 'Stethoscope', 'Activity',
  'Briefcase', 'Users', 'User', 'Building',
  'MoreHorizontal', 'Settings', 'HelpCircle', 'Info', 'AlertCircle',
  'CheckCircle', 'XCircle', 'PlusCircle', 'MinusCircle',
  'Sun', 'Moon', 'Cloud', 'Umbrella', 'Zap', 'Droplets', 'Flame',
  'Star', 'Heart', 'Smile', 'Frown',
  'Search', 'Plus', 'Minus', 'Check', 'X', 'ArrowUp', 'ArrowDown',
  'RefreshCw', 'Download', 'Upload', 'Share2', 'ExternalLink',
  'MapPin', 'Calendar', 'Clock', 'Bell', 'Mail',
  'Trash2', 'Edit3', 'Copy', 'Save', 'Printer',
  'Lock', 'Unlock', 'Eye', 'EyeOff', 'Shield',
  'Link2', 'Paperclip', 'Image', 'Video', 'Volume2',
]
const backendDefaultFiles = [
  'backend/MyExpenses.Api/Services/DbInitializer.cs',
  'backend/MyExpenses.Api/Endpoints/CategoryEndpoints.cs',
  'backend/MyExpenses.Api/Endpoints/PaymentMethodEndpoints.cs',
]

// 遞迴找出前端所有 Vue source files，供靜態圖示名稱掃描使用。
function collectVueFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const entryPath = resolve(directory, entry.name)
    return entry.isDirectory() ? collectVueFiles(entryPath) : entry.name.endsWith('.vue') ? [entryPath] : []
  })
}

// 從 Vue template 擷取非動態的共用 Icon 名稱。
function extractLiteralIconNames(source: string): string[] {
  return [...source.matchAll(/<Icon\b[^>]*(?<![:\w-])name\s*=\s*(['"])([^'"]+)\1/g)].map((match) => match[2])
}

// 從 backend 內建資料定義擷取 category 與 payment-method 的圖示名稱。
function extractBackendIconNames(): string[] {
  return backendDefaultFiles.flatMap((relativePath) => {
    const source = readFileSync(resolve(repoRoot, relativePath), 'utf8')
    return [...source.matchAll(/Icon\s*=\s*"([^"]+)"/g)].map((match) => match[1])
  })
}

// 驗證 registry 是否涵蓋所有應由共用元件解析的圖示來源。
describe('icon registry coverage', () => {
  // 確保 picker 的排序與既有可選名稱完全相容。
  test('exports the existing ordered IconPicker choices', () => {
    expect(pickerIconNames).toEqual(expectedPickerIconNames)
  })

  // 確保每個 picker 選項都對應到明確註冊的 Lucide component。
  test('resolves every IconPicker choice from the explicit registry', () => {
    for (const name of expectedPickerIconNames) {
      expect(resolveIcon(name), name).toBe(iconRegistry[normalizeIconName(name)])
    }
  })

  // 確保前端 template 中的靜態 Icon 名稱不會遺漏。
  test('resolves every literal shared Icon name in Vue source', () => {
    const names = collectVueFiles(frontendSourceRoot).flatMap((filePath) =>
      extractLiteralIconNames(readFileSync(filePath, 'utf8')),
    )

    for (const name of new Set(names)) {
      expect(resolveIcon(name), name).toBe(iconRegistry[normalizeIconName(name)])
    }
  })

  // 確保 source scanner 支援單引號與等號周圍空白，且略過動態 name binding。
  test('scans literal Icon names across Vue attribute formatting', () => {
    expect(extractLiteralIconNames(
      `<Icon name = 'Circle' /><Icon :name="dynamicName" /><Icon name="Wallet" />`,
    )).toEqual(['Circle', 'Wallet'])
  })

  // 確保 backend 內建 category 與 payment-method 圖示都能安全解析。
  test('resolves every backend built-in category and payment-method icon', () => {
    for (const name of new Set(extractBackendIconNames())) {
      expect(resolveIcon(name), name).toBe(iconRegistry[normalizeIconName(name)])
    }
  })

  // 確保 registry 以 named imports 建立，而非重新引入完整 Lucide namespace。
  test('declares explicit Lucide imports for supported names', () => {
    expect(registrySource).toMatch(/import\s*\{[\s\S]+\}\s*from\s*['"]@lucide\/vue['"]/
    )
    expect(registrySource).not.toMatch(/import\s+\*\s+as\s+\w+\s+from\s+['"]@lucide\/vue['"]/
    )
  })
})
