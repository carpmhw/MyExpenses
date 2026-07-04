<script setup lang="ts">
import { ref, computed, inject, watch, onMounted } from 'vue'
import { api } from '../../api'
import type { Stock, StockInstrumentType, StockListItem } from '../../types'
import Card from '../../components/ui/Card.vue'
import Button from '../../components/ui/Button.vue'
import DataTable from '../../components/ui/DataTable.vue'
import Modal from '../../components/ui/Modal.vue'
import ConfirmDialog from '../../components/ui/ConfirmDialog.vue'
import Input from '../../components/ui/Input.vue'
import Icon from '../../components/ui/Icon.vue'
import { formatMoney, formatShares } from '../../utils/format'
import { STOCK_INSTRUMENT_TYPE_OPTIONS, formatStockInstrumentType } from '../../utils/stock'
import { usePagination } from '../../composables/usePagination'

const toast = inject<{ success: (m: string) => void; error: (m: string) => void }>('toast')!

const pagination = usePagination(1, 15)

const stocks = ref<StockListItem[]>([])
const loading = ref(false)
const saving = ref(false)

const modalOpen = ref(false)
const editingItem = ref<StockListItem | null>(null)
const form = ref({ name: '', symbol: '', instrumentType: 'Stock' as StockInstrumentType, shares: 0, buyPrice: 0, currentPrice: 0, broker: '', lastPriceUpdate: null as string | null })
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
  fresh: 'text-green-600',
  warning: 'text-amber-500',
  stale: 'text-red-500',
}

const confirmOpen = ref(false)
const deletingId = ref<number | null>(null)
const snapshotLoading = ref(false)

const columns = [
  { key: 'seq', label: '序號' },
  { key: 'name', label: '名稱' },
  { key: 'symbol', label: '代號' },
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

// Builds the stock payload with normalized text fields before it is sent to the API.
function buildStockPayload(): Omit<Stock, 'id'> {
  return {
    name: form.value.name.trim(),
    symbol: form.value.symbol.trim(),
    instrumentType: form.value.instrumentType,
    shares: form.value.shares,
    buyPrice: form.value.buyPrice,
    currentPrice: form.value.currentPrice,
    broker: form.value.broker.trim(),
    lastPriceUpdate: form.value.lastPriceUpdate,
  }
}

function openCreate() {
  editingItem.value = null
  form.value = { name: '', symbol: '', instrumentType: 'Stock', shares: 0, buyPrice: 0, currentPrice: 0, broker: '', lastPriceUpdate: null }
  modalOpen.value = true
}

function openEdit(item: StockListItem) {
  editingItem.value = item
  form.value = {
    name: item.name,
    symbol: item.symbol,
    instrumentType: item.instrumentType,
    shares: item.shares,
    buyPrice: item.buyPrice,
    currentPrice: item.currentPrice,
    broker: item.broker || '',
    lastPriceUpdate: item.lastPriceUpdate,
  }
  syncPrice.value = true
  modalOpen.value = true
}

async function save() {
  const errs = formErrors.value
  if (Object.keys(errs).length > 0) return

  saving.value = true
  try {
    if (syncPrice.value && editingItem.value && form.value.symbol?.trim()) {
      try {
        const result = await api.stocks.lookup(form.value.symbol.trim())
        if (result.currentPrice != null) {
          form.value.currentPrice = result.currentPrice
        }
      } catch {
        // lookup failed, proceed with existing price
      }
    }

    if (editingItem.value) {
      const payload = buildStockPayload()
      await api.stocks.update(editingItem.value.id, {
        ...payload,
        lastPriceUpdate: syncPrice.value ? new Date().toISOString() : undefined,
      })
      toast.success('股票已更新')
    } else {
      await api.stocks.create(buildStockPayload())
      toast.success('股票已建立')
    }
    modalOpen.value = false
    await fetchStocks()
  } catch (e) {
    toast.error(e instanceof Error ? e.message : '儲存失敗')
  } finally {
    saving.value = false
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

watch(() => form.value.symbol, (val) => {
  if (lookupTimer) clearTimeout(lookupTimer)
  if (!val?.trim() || editingItem.value) return
  lookupTimer = setTimeout(async () => {
    try {
      const result = await api.stocks.lookup(val.trim())
      if (!editingItem.value) {
        if (result.name) form.value.name = result.name
        if (result.currentPrice != null) form.value.currentPrice = result.currentPrice
      }
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
        <p class="text-xs text-text-secondary mt-1">所有持股記錄 · Stocks · 每個交易日 23:00（台灣時間）自動更新股價</p>
      </div>
      <div class="flex items-center gap-2">
        <Button :loading="snapshotLoading" @click="takeSnapshot" title="紀錄所有銀行帳戶與股票的當前狀態">📷 拍照</Button>
        <Button @click="openCreate">+ 新增股票</Button>
      </div>
    </div>

    <div class="grid grid-cols-1 sm:grid-cols-3 gap-4 mb-6">
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl bg-blue-500 dark:bg-blue-700 flex items-center justify-center">
            <Icon name="wallet" :size="22" class="text-white" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">預估賣出淨值</p>
            <p class="text-xl font-bold text-text-primary">{{ formatMoney(stats.totalValue) }}</p>
          </div>
        </div>
      </Card>
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl flex items-center justify-center" :class="stats.totalPnl >= 0 ? 'bg-green-500 dark:bg-green-700' : 'bg-red-500 dark:bg-red-700'">
            <Icon name="trending-up" :size="22" class="text-white" />
          </div>
          <div>
            <p class="text-xs text-text-secondary">預估損益</p>
            <p class="text-xl font-bold" :class="stats.totalPnl >= 0 ? 'text-green-600' : 'text-red-600'">{{ formatMoney(stats.totalPnl) }}</p>
          </div>
        </div>
      </Card>
      <Card>
        <div class="flex items-center gap-4">
          <div class="w-11 h-11 rounded-xl bg-amber-500 dark:bg-amber-700 flex items-center justify-center">
            <Icon name="shopping-bag" :size="22" class="text-white" />
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
            class="w-full sm:w-48 px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary placeholder:text-text-tertiary"
          />
        </div>
        <div class="flex flex-wrap items-center gap-2">
          <span class="text-sm font-medium text-text-primary">券商</span>
          <input
            v-model="brokerFilter"
            type="text"
            placeholder="輸入券商關鍵字"
            class="w-full sm:w-56 px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary placeholder:text-text-tertiary"
          />
        </div>
      </div>
      <DataTable :columns="columns" :loading="loading" :items="stocks">
        <template #empty>
          <div class="text-center text-text-tertiary py-4">尚無股票資料</div>
        </template>
        <tr v-for="(item, idx) in stocks" :key="item.id" class="border-b border-border-default hover:bg-gray-100 dark:hover:bg-gray-700">
          <td class="py-3 px-4 text-text-secondary text-sm w-[60px]">{{ (pagination.page.value - 1) * pagination.pageSize.value + idx + 1 }}</td>
          <td class="py-3 px-4 text-text-primary font-medium">{{ item.name }}</td>
          <td class="py-3 px-4 text-text-secondary font-mono">{{ item.symbol }}</td>
          <td class="py-3 px-4 text-text-secondary text-sm whitespace-nowrap">{{ formatStockInstrumentType(item.instrumentType) }}</td>
          <td class="py-3 px-4 text-text-primary text-sm">{{ formatShares(item.shares) }}</td>
          <td class="py-3 px-4 text-text-primary text-sm text-right">{{ formatMoney(item.buyPrice) }}</td>
          <td class="py-3 px-4 text-text-primary text-sm text-right" :class="freshnessColors[priceFreshness(item.lastPriceUpdate)]">{{ formatMoney(item.currentPrice) }}</td>
          <td class="py-3 px-4 text-sm text-right font-semibold" :class="item.estimatedGainLoss >= 0 ? 'text-green-600' : 'text-red-600'">
            {{ formatMoney(item.estimatedGainLoss) }}
          </td>
          <td class="py-3 px-4 text-text-secondary text-sm">{{ item.broker }}</td>
          <td class="py-3 px-4 w-[80px]">
            <div class="flex items-center gap-1">
              <button
                class="p-1.5 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 text-text-secondary cursor-pointer transition-colors"
                @click="openEdit(item)"
              >
                <Icon name="pencil" :size="16" />
              </button>
              <button
                class="p-1.5 rounded-lg hover:bg-gray-100 dark:hover:bg-gray-700 text-red-500 cursor-pointer transition-colors"
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

    <Modal :open="modalOpen" :title="editingItem ? '編輯股票' : '新增股票'" @update:open="modalOpen = $event">
      <form class="space-y-4" @submit.prevent="save">
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">代號</label>
          <Input v-model="form.symbol" placeholder="e.g. 2330" />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">名稱</label>
          <Input v-model="form.name" :error="formErrors.name" placeholder="e.g. 台積電" />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">商品類型</label>
          <select
            v-model="form.instrumentType"
            class="w-full px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 focus:border-accent-primary"
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
            @update:model-value="form.currentPrice = Number($event) || 0"
          />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">券商</label>
          <Input v-model="form.broker" placeholder="e.g. 元大證券" />
        </div>
        <div v-if="editingItem" class="flex items-center gap-2">
          <input id="syncPrice" type="checkbox" v-model="syncPrice" class="w-4 h-4 rounded border-border-default text-primary-600 focus:ring-primary-500" />
          <label for="syncPrice" class="text-sm text-text-secondary cursor-pointer">同步更新目前股價</label>
        </div>
        <div class="flex justify-end gap-3 pt-2">
          <Button variant="ghost" type="button" @click="modalOpen = false">取消</Button>
          <Button type="submit" :loading="saving">儲存</Button>
        </div>
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
