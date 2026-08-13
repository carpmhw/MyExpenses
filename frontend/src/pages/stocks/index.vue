<script setup lang="ts">
import { ref, computed, inject, watch, onMounted } from 'vue'
import { api } from '../../api'
import type { Stock, StockListItem } from '../../types'
import Card from '../../components/ui/Card.vue'
import Button from '../../components/ui/Button.vue'
import DataTable from '../../components/ui/DataTable.vue'
import Modal from '../../components/ui/Modal.vue'
import ConfirmDialog from '../../components/ui/ConfirmDialog.vue'
import Input from '../../components/ui/Input.vue'
import Icon from '../../components/ui/Icon.vue'
import { formatMoney, formatShares } from '../../utils/format'
import {
  STOCK_INSTRUMENT_TYPE_OPTIONS,
  STOCK_MARKET_OPTIONS,
  formatStockInstrumentType,
  formatStockMarket,
} from '../../utils/stock'
import { syncStockPriceOnSave } from '../../utils/stockPriceSync'
import { usePagination } from '../../composables/usePagination'
import { applyStockMarketLookup, resetStockMarketLookupFields } from '../../utils/stockMarketLookup'

const toast = inject<{ success: (m: string) => void; error: (m: string) => void }>('toast')!

const pagination = usePagination(1, 15)

const stocks = ref<StockListItem[]>([])
const loading = ref(false)
const saving = ref(false)

const modalOpen = ref(false)
const editingItem = ref<StockListItem | null>(null)
type StockFormState = Omit<Stock, 'id' | 'broker'> & { broker: string }
const form = ref<StockFormState>({ name: '', symbol: '', market: 'Unknown', instrumentType: 'Stock', shares: 0, buyPrice: 0, currentPrice: 0, broker: '', lastPriceUpdate: null })
const marketDirty = ref(false)
const lookupDirty = ref({ name: false, currentPrice: false })
const syncPrice = ref(true)
const totalEstimatedNetSellValue = ref(0)
const totalEstimatedGainLoss = ref(0)
const symbolFilter = ref('')
const brokerFilter = ref('')

function priceFreshness(lastUpdate: string | null): 'fresh' | 'warning' | 'stale' {
  if (!lastUpdate) return 'stale'
  const daysSinceUpdate = Math.floor(
    (Date.now() - new Date(lastUpdate).getTime()) / (1000 * 60 * 60 * 24)
  )
  if (daysSinceUpdate <= 1) return 'fresh'
  if (daysSinceUpdate <= 3) return 'warning'
  return 'stale'
}

const freshnessColors: Record<string, string> = {
  fresh: 'text-color-income-text',
  warning: 'text-color-warning-text',
  stale: 'text-color-expense-text',
}

const confirmOpen = ref(false)
const deletingId = ref<number | null>(null)
const snapshotLoading = ref(false)

const columns = [
  { key: 'seq', label: '序號' },
  { key: 'name', label: '名稱' },
  { key: 'symbol', label: '代號' },
  { key: 'market', label: '市場' },
  { key: 'instrumentType', label: '商品類型' },
  { key: 'shares', label: '股數' },
  { key: 'buyPrice', label: '買入均價', align: 'right' as const },
  { key: 'currentPrice', label: '現價', align: 'right' as const },
  { key: 'pnl', label: '預估損益', align: 'right' as const },
  { key: 'broker', label: '券商' },
]

const formErrors = computed(() => {
  const errs: Record<string, string> = {}
  if (!form.value.name?.trim()) errs.name = '請填寫股票名稱'
  if (form.value.shares <= 0) errs.shares = '股數必須大於零'
  if (form.value.buyPrice <= 0) errs.buyPrice = '買入均價必須大於零'
  return errs
})

const stats = computed(() => {
  const totalValue = totalEstimatedNetSellValue.value
  const totalPnl = totalEstimatedGainLoss.value
  return { totalValue, totalPnl, count: pagination.total.value }
})

async function fetchStocks() {
  loading.value = true
  try {
    const result = await api.stocks.list({
      page: pagination.page.value,
      pageSize: pagination.pageSize.value,
      symbol: symbolFilter.value,
      broker: brokerFilter.value,
    })
    stocks.value = result.items
    pagination.total.value = result.total
    totalEstimatedNetSellValue.value = result.totalEstimatedNetSellValue
    totalEstimatedGainLoss.value = result.totalEstimatedGainLoss
  } finally {
    loading.value = false
  }
}

watch(() => pagination.page.value, () => fetchStocks())

watch([symbolFilter, brokerFilter], () => {
  if (pagination.page.value !== 1) {
    pagination.page.value = 1
    return
  }
  fetchStocks()
})

/** 依指定表單快照建立正規化持股 payload，避免非同步期間混入其他表單資料。 */
function buildStockPayload(formState: StockFormState): Omit<Stock, 'id'> {
  return {
    name: formState.name.trim(),
    symbol: formState.symbol.trim(),
    market: formState.market,
    instrumentType: formState.instrumentType,
    shares: formState.shares,
    buyPrice: formState.buyPrice,
    currentPrice: formState.currentPrice,
    broker: formState.broker.trim(),
    lastPriceUpdate: formState.lastPriceUpdate,
  }
}

/** 開啟新增表單前使前一個表單的 symbol lookup 失效。 */
function openCreate() {
  invalidatePendingStockLookup()
  editingItem.value = null
  form.value = { name: '', symbol: '', market: 'Unknown', instrumentType: 'Stock', shares: 0, buyPrice: 0, currentPrice: 0, broker: '', lastPriceUpdate: null }
  marketDirty.value = false
  lookupDirty.value = { name: false, currentPrice: false }
  modalOpen.value = true
}

/** 開啟指定持股編輯表單前使前一個表單的 symbol lookup 失效。 */
function openEdit(item: StockListItem) {
  invalidatePendingStockLookup()
  editingItem.value = item
  form.value = {
    name: item.name,
    symbol: item.symbol,
    market: item.market,
    instrumentType: item.instrumentType,
    shares: item.shares,
    buyPrice: item.buyPrice,
    currentPrice: item.currentPrice,
    broker: item.broker || '',
    lastPriceUpdate: item.lastPriceUpdate,
  }
  syncPrice.value = true
  marketDirty.value = true
  modalOpen.value = true
}

/** 標記使用者手動選擇市場，避免後續 lookup 覆寫意圖。 */
function markMarketDirty() {
  if (!editingItem.value && modalOpen.value)
    marketDirty.value = true
}

/** 儲存持股，編輯時依使用者選擇的市場同步同市場最新股價。 */
async function save() {
  const errs = formErrors.value
  if (Object.keys(errs).length > 0) return

  invalidatePendingStockLookup()
  const editingSnapshot = editingItem.value
  const formIdentity = form.value
  const formSnapshot = { ...formIdentity }
  const payloadSnapshot = buildStockPayload(formSnapshot)
  const syncPriceSnapshot = syncPrice.value
  let mutationSucceeded = false
  saving.value = true
  try {
    if (editingSnapshot) {
      const priceSyncResult = await syncStockPriceOnSave(
        syncPriceSnapshot,
        formSnapshot.symbol,
        {
          currentPrice: formSnapshot.currentPrice,
          lastPriceUpdate: formSnapshot.lastPriceUpdate,
        },
        (symbol) => api.stocks.lookup(symbol),
        () => new Date().toISOString(),
        formSnapshot.market,
      )

      await api.stocks.update(editingSnapshot.id, {
        ...payloadSnapshot,
        currentPrice: priceSyncResult.currentPrice,
        lastPriceUpdate: priceSyncResult.lastPriceUpdate,
      })
      if (editingItem.value === editingSnapshot && form.value === formIdentity) {
        form.value.currentPrice = priceSyncResult.currentPrice
        form.value.lastPriceUpdate = priceSyncResult.lastPriceUpdate
        modalOpen.value = false
      }
      if (priceSyncResult.status === 'failed') {
        toast.error('股票已更新，但取得最新股價失敗')
      } else {
        toast.success('股票已更新')
      }
      mutationSucceeded = true
    } else {
      await api.stocks.create(payloadSnapshot)
      if (editingItem.value === editingSnapshot && form.value === formIdentity)
        modalOpen.value = false
      toast.success('股票已建立')
      mutationSucceeded = true
    }
  } catch (e) {
    toast.error(e instanceof Error ? e.message : '儲存失敗')
  } finally {
    saving.value = false
  }

  if (mutationSucceeded) {
    try {
      await fetchStocks()
    } catch {
      toast.error('股票已儲存，但重新整理清單失敗')
    }
  }
}

function confirmDelete(id: number) {
  deletingId.value = id
  confirmOpen.value = true
}

async function doDelete() {
  if (deletingId.value !== null) {
    try {
      await api.stocks.delete(deletingId.value)
      confirmOpen.value = false
      deletingId.value = null
      toast.success('股票已刪除')
      await fetchStocks()
    } catch (e) {
      toast.error(e instanceof Error ? e.message : '刪除失敗')
    }
  }
}

let lookupTimer: ReturnType<typeof setTimeout> | null = null
let lookupRequestId = 0

/** 取消已排程的 symbol lookup，並使進行中的晚到 response 失效。 */
function invalidatePendingStockLookup() {
  lookupRequestId++
  if (lookupTimer) {
    clearTimeout(lookupTimer)
    lookupTimer = null
  }
}

// 代號 identity 改變時立即清除未手動修改的 lookup 衍生值，再查詢新代號。
watch(() => form.value.symbol, (val, previousVal) => {
  const requestId = ++lookupRequestId
  if (lookupTimer) clearTimeout(lookupTimer)
  const symbolChanged = val.trim().toUpperCase() !== previousVal.trim().toUpperCase()
  if (symbolChanged && !editingItem.value) {
    const lookupState = resetStockMarketLookupFields(
      {
        name: form.value.name,
        symbol: form.value.symbol,
        market: form.value.market,
        currentPrice: form.value.currentPrice,
      },
      { ...lookupDirty.value, market: marketDirty.value },
    )
    form.value = { ...form.value, ...lookupState }
  }
  if (!val?.trim() || editingItem.value) return
  lookupTimer = setTimeout(async () => {
    const requestedSymbol = val.trim()
    try {
      const result = await api.stocks.lookup(requestedSymbol)
      if (editingItem.value || requestId !== lookupRequestId) return
      const lookupState = applyStockMarketLookup(
        {
          name: form.value.name,
          symbol: form.value.symbol,
          market: form.value.market,
          currentPrice: form.value.currentPrice,
        },
        result,
        requestedSymbol,
        form.value.symbol,
        marketDirty.value,
        lookupDirty.value,
      )
      form.value = { ...form.value, ...lookupState }
    } catch {
      // lookup failed, values stay as-is
    }
  }, 400)
})

async function takeSnapshot() {
  snapshotLoading.value = true
  try {
    const result = await api.snapshots.create()
    toast.success(`快照已建立: ${result.name}`)
  } catch (e) {
    toast.error(e instanceof Error ? e.message : '建立快照失敗')
  } finally {
    snapshotLoading.value = false
  }
}

onMounted(fetchStocks)
</script>

<template>
  <div class="p-4 lg:p-6">
    <div class="flex items-center justify-between mb-6">
      <div>
        <h1 class="text-2xl font-bold text-text-primary">股票管理</h1>
        <p class="text-xs text-text-secondary mt-1">所有持股記錄 · Stocks · 價格由業務排程服務自動更新</p>
      </div>
      <div class="flex items-center gap-2">
        <Button :loading="snapshotLoading" @click="takeSnapshot" title="紀錄所有銀行帳戶與股票的當前狀態">📷 拍照</Button>
        <Button @click="openCreate">+ 新增股票</Button>
      </div>
    </div>

    <div class="grid grid-cols-1 sm:grid-cols-3 gap-4 mb-6">
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl bg-color-info flex items-center justify-center">
            <Icon name="wallet" :size="22" class="text-text-on-accent" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">預估賣出淨值</p>
            <p class="text-xl font-bold text-text-primary">{{ formatMoney(stats.totalValue) }}</p>
          </div>
        </div>
      </Card>
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl flex items-center justify-center" :class="stats.totalPnl >= 0 ? 'bg-color-income' : 'bg-color-expense-action'">
            <Icon name="trending-up" :size="22" class="text-text-on-accent" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">預估損益</p>
            <p class="text-xl font-bold" :class="stats.totalPnl >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">{{ formatMoney(stats.totalPnl) }}</p>
          </div>
        </div>
      </Card>
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl bg-color-warning flex items-center justify-center">
            <Icon name="shopping-bag" :size="22" class="text-text-on-accent" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">持股檔數</p>
            <p class="text-xl font-bold text-text-primary">{{ stats.count }} 檔</p>
          </div>
        </div>
      </Card>
    </div>

    <Card>
      <div class="flex flex-wrap items-center gap-3 mb-4">
        <div class="flex flex-wrap items-center gap-2">
          <span class="text-sm font-medium text-text-primary">代號</span>
          <input
            v-model="symbolFilter"
            type="text"
            placeholder="輸入股票代號"
            class="w-full sm:w-48 px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary placeholder:text-text-tertiary"
          />
        </div>
        <div class="flex flex-wrap items-center gap-2">
          <span class="text-sm font-medium text-text-primary">券商</span>
          <input
            v-model="brokerFilter"
            type="text"
            placeholder="輸入券商關鍵字"
            class="w-full sm:w-56 px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary placeholder:text-text-tertiary"
          />
        </div>
      </div>
      <DataTable :columns="columns" :loading="loading" :items="stocks">
        <template #empty>
          <div class="text-center text-text-tertiary py-4">尚無股票資料</div>
        </template>
        <tr v-for="(item, idx) in stocks" :key="item.id" class="border-b border-border-default hover:bg-bg-raised">
          <td class="py-3 px-4 text-text-secondary text-sm w-[60px]">{{ (pagination.page.value - 1) * pagination.pageSize.value + idx + 1 }}</td>
          <td class="py-3 px-4 text-text-primary font-medium">{{ item.name }}</td>
            <td class="py-3 px-4 text-text-secondary font-mono">{{ item.symbol }}</td>
           <td class="py-3 px-4 text-text-secondary text-sm whitespace-nowrap">{{ formatStockMarket(item.market) }}</td>
           <td class="py-3 px-4 text-text-secondary text-sm whitespace-nowrap">{{ formatStockInstrumentType(item.instrumentType) }}</td>
          <td class="py-3 px-4 text-text-primary text-sm">{{ formatShares(item.shares) }}</td>
          <td class="py-3 px-4 text-text-primary text-sm text-right">{{ formatMoney(item.buyPrice) }}</td>
          <td class="py-3 px-4 text-text-primary text-sm text-right" :class="freshnessColors[priceFreshness(item.lastPriceUpdate)]">{{ formatMoney(item.currentPrice) }}</td>
          <td class="py-3 px-4 text-sm text-right font-semibold" :class="item.estimatedGainLoss >= 0 ? 'text-color-income-text' : 'text-color-expense-text'">
            {{ formatMoney(item.estimatedGainLoss) }}
          </td>
          <td class="py-3 px-4 text-text-secondary text-sm">{{ item.broker }}</td>
          <td class="py-3 px-4 w-[80px]">
            <div class="flex items-center gap-1">
              <button
                class="p-1.5 rounded-lg hover:bg-bg-raised text-text-secondary cursor-pointer transition-colors"
                @click="openEdit(item)"
              >
                <Icon name="pencil" :size="16" />
              </button>
              <button
                class="p-1.5 rounded-lg hover:bg-bg-raised text-color-expense-text cursor-pointer transition-colors"
                @click="confirmDelete(item.id)"
              >
                <Icon name="trash-2" :size="16" />
              </button>
            </div>
          </td>
        </tr>
      </DataTable>
      <div class="flex items-center justify-between px-4 py-3 border-t border-border-default">
        <span class="text-sm text-text-secondary">共 {{ pagination.total.value }} 筆</span>
        <div class="flex items-center gap-2">
          <Button variant="ghost" :disabled="!pagination.hasPrev.value" @click="pagination.prev()">
            上一頁
          </Button>
          <span class="text-sm text-text-secondary">{{ pagination.page.value }} / {{ pagination.totalPages.value }}</span>
          <Button variant="ghost" :disabled="!pagination.hasNext.value" @click="pagination.next()">
            下一頁
          </Button>
        </div>
      </div>
    </Card>

    <Modal :open="modalOpen" :title="editingItem ? '編輯股票' : '新增股票'" :close-disabled="saving" @update:open="modalOpen = $event">
      <form @submit.prevent="save">
        <fieldset :disabled="saving" class="m-0 min-w-0 space-y-4 border-0 p-0">
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">代號</label>
          <Input v-model="form.symbol" placeholder="e.g. 2330" />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">名稱</label>
          <Input v-model="form.name" :error="formErrors.name" placeholder="e.g. 台積電" @update:model-value="lookupDirty.name = true" />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">交易市場</label>
          <select
           v-model="form.market"
           @change="markMarketDirty"
           class="w-full px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
          >
            <option v-for="option in STOCK_MARKET_OPTIONS" :key="option.value" :value="option.value">
              {{ option.label }}
            </option>
          </select>
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">商品類型</label>
          <select
            v-model="form.instrumentType"
            class="w-full px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
          >
            <option v-for="option in STOCK_INSTRUMENT_TYPE_OPTIONS" :key="option.value" :value="option.value">
              {{ option.label }}
            </option>
          </select>
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">股數</label>
          <Input
            :model-value="form.shares || ''"
            type="number"
            step="1"
            :error="formErrors.shares"
            @update:model-value="form.shares = Number($event) || 0"
          />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">買入均價</label>
          <Input
            :model-value="form.buyPrice || ''"
            type="number"
            step="0.01"
            :error="formErrors.buyPrice"
            @update:model-value="form.buyPrice = Number($event) || 0"
          />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">現價</label>
          <Input
            :model-value="form.currentPrice || ''"
            type="number"
            step="0.01"
            @update:model-value="lookupDirty.currentPrice = true; form.currentPrice = Number($event) || 0"
          />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">券商</label>
          <Input v-model="form.broker" placeholder="e.g. 元大證券" />
        </div>
        <div v-if="editingItem" class="flex items-center gap-2">
          <input id="syncPrice" type="checkbox" v-model="syncPrice" class="w-4 h-4 rounded border-border-strong text-primary-600 focus:ring-focus-ring" />
           <label for="syncPrice" class="text-sm text-text-secondary cursor-pointer">儲存時取得最新股價</label>
        </div>
        <div class="flex justify-end gap-3 pt-2">
          <Button variant="ghost" type="button" :disabled="saving" @click="modalOpen = false">取消</Button>
          <Button type="submit" :loading="saving">儲存</Button>
        </div>
        </fieldset>
      </form>
    </Modal>

    <ConfirmDialog
      :open="confirmOpen"
      title="刪除股票"
      description="確定要刪除此股票記錄嗎？此操作無法復原。"
      variant="danger"
      @update:open="confirmOpen = $event"
      @confirm="doDelete"
    />
  </div>
</template>
