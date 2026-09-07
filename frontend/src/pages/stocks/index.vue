<script setup lang="ts">
import { ref, computed, inject, watch, onMounted, nextTick } from 'vue'
import { api } from '../../api'
import type {
  EditableStockTransactionType,
  Stock,
  StockLedgerTransactionRequest,
  StockListItem,
  StockOption,
  StockOptionsStatus,
  StockTransactionListItem,
  StockTransactionType,
} from '../../types'
import Card from '../../components/ui/Card.vue'
import Button from '../../components/ui/Button.vue'
import Modal from '../../components/ui/Modal.vue'
import ConfirmDialog from '../../components/ui/ConfirmDialog.vue'
import Input from '../../components/ui/Input.vue'
import Icon from '../../components/ui/Icon.vue'
import StockHoldingsTable from '../../components/stocks/StockHoldingsTable.vue'
import StockTransactionLedger from '../../components/stocks/StockTransactionLedger.vue'
import StockLedgerInitialization from '../../components/stocks/StockLedgerInitialization.vue'
import StockTransactionModal from '../../components/stocks/StockTransactionModal.vue'
import { formatMoney } from '../../utils/format'
import {
  STOCK_INSTRUMENT_TYPE_OPTIONS,
  STOCK_MARKET_OPTIONS,
} from '../../utils/stock'
import { syncStockPriceOnSave } from '../../utils/stockPriceSync'
import { usePagination } from '../../composables/usePagination'
import { applyStockMarketLookup, resetStockMarketLookupFields } from '../../utils/stockMarketLookup'
import { useTimeZone } from '../../composables/useTimeZone'

const toast = inject<{ success: (m: string) => void; error: (m: string) => void }>('toast')!
const timeZone = useTimeZone()

const pagination = usePagination(1, 15)

const stocks = ref<StockListItem[]>([])
const stockOptions = ref<StockOption[]>([])
const stockOptionsLoaded = ref(false)
const stockOptionsLoading = ref(false)
const stockOptionsStatus = ref<StockOptionsStatus>('idle')
let stockOptionsRequestId = 0
let stockOptionsRequest: Promise<void> | null = null
const loading = ref(false)
const saving = ref(false)

const modalOpen = ref(false)
const editingItem = ref<StockListItem | null>(null)
type StockFormState = Omit<Stock, 'id' | 'broker'> & {
  broker: string
  tradeDate: string
  initialTransactionType: 'Buy' | 'OpeningBalance'
}
// 建立新增股票表單的預設值，初始交易與股票建立將由 atomic position command 一次完成。
function createEmptyStockForm(): StockFormState {
  return {
    name: '',
    symbol: '',
    market: 'Unknown',
    instrumentType: 'Stock',
    shares: 0,
    buyPrice: 0,
    currentPrice: 0,
    broker: '',
    lastPriceUpdate: null,
    tradeDate: timeZone.getToday(),
    initialTransactionType: 'Buy',
  }
}

const form = ref<StockFormState>(createEmptyStockForm())
const marketDirty = ref(false)
const lookupDirty = ref({ name: false, currentPrice: false })
const syncPrice = ref(true)
const totalEstimatedNetSellValue = ref(0)
const totalEstimatedGainLoss = ref(0)
const symbolFilter = ref('')
const brokerFilter = ref('')
const includeClosed = ref(false)
const activeTab = ref<'holdings' | 'ledger'>('holdings')
const ledgerItems = ref<StockTransactionListItem[]>([])
const ledgerLoading = ref(false)
const ledgerPage = ref(1)
const ledgerTotal = ref(0)
const ledgerPageSize = 20
const ledgerStockId = ref<number | null>(null)
const ledgerType = ref<StockTransactionType | ''>('')
const ledgerDateStart = ref('')
const ledgerDateEnd = ref('')
const transactionModalOpen = ref(false)
const transactionStockId = ref<number | null>(null)
const transactionInitialType = ref<EditableStockTransactionType>('Buy')
const transactionEditing = ref<StockTransactionListItem | null>(null)
const transactionSaving = ref(false)
const transactionError = ref('')
const initializationLoading = ref(false)
const initializationResponse = ref<Awaited<ReturnType<typeof api.stocks.ledger.initialize>> | null>(null)

const transactionStockIdentity = computed<Pick<StockOption, 'id' | 'name' | 'symbol' | 'broker'> | null>(() => {
  const stock = stocks.value.find(item => item.id === transactionStockId.value)
  if (stock) {
    return {
      id: stock.id,
      name: stock.name,
      symbol: stock.symbol,
      broker: stock.broker,
    }
  }
  const transaction = transactionEditing.value
  return transaction && transaction.stockId === transactionStockId.value
    ? {
        id: transaction.stockId,
        name: transaction.stockName,
        symbol: transaction.symbol,
        broker: transaction.broker,
      }
    : null
})

const editingLedgerManaged = computed(() => editingItem.value?.hasLedger === true)
const ledgerMarketLocked = computed(() => editingLedgerManaged.value && editingItem.value?.market !== 'Unknown')
const hasUninitializedActiveHoldings = computed(() => stocks.value.some(stock => stock.shares > 0 && stock.hasLedger !== true))

const confirmOpen = ref(false)
const deletingId = ref<number | null>(null)
const transactionConfirmOpen = ref(false)
const deletingTransactionId = ref<number | null>(null)
const snapshotLoading = ref(false)

const formErrors = computed(() => {
  const errs: Record<string, string> = {}
  if (!form.value.name?.trim()) errs.name = '請填寫股票名稱'
  if (!editingLedgerManaged.value) {
    if (form.value.shares <= 0) errs.shares = '股數必須大於零'
    if (form.value.buyPrice <= 0) errs.buyPrice = '買入均價必須大於零'
  }
  if (!editingItem.value && !form.value.tradeDate) errs.tradeDate = '請選擇初始交易日期'
  if (!editingItem.value && form.value.initialTransactionType === 'OpeningBalance' && form.value.currentPrice <= 0) {
    errs.currentPrice = '既有部位帶入需要目前價格'
  }
  return errs
})

// 將持股列表 row 降為交易 selector 所需的輕量 option，避免依賴估值欄位。
function toStockOption(stock: StockListItem): StockOption {
  return {
    id: stock.id,
    name: stock.name,
    symbol: stock.symbol,
    broker: stock.broker,
    shares: stock.shares,
    hasLedger: stock.hasLedger,
  }
}

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
      includeClosed: includeClosed.value,
    })
    stocks.value = result.items
    if (!stockOptionsLoaded.value && stockOptionsStatus.value !== 'loading')
      stockOptions.value = result.items.map(toStockOption)
    pagination.total.value = result.total
    totalEstimatedNetSellValue.value = result.totalEstimatedNetSellValue
    totalEstimatedGainLoss.value = result.totalEstimatedGainLoss
  } finally {
    loading.value = false
  }
}

// 第一次進入交易流程時載入完整 options，並以持股列表作為載入期間的 fallback。
async function loadStockOptions(): Promise<void> {
  if (stockOptionsLoaded.value || stockOptionsLoading.value) return
  if (stockOptionsRequest) return stockOptionsRequest
  return requestStockOptions('載入交易股票清單失敗')
}

// 以 request generation 丟棄過期 response，避免 mutation 後的舊 options 覆寫最新股數。
async function requestStockOptions(errorMessage: string): Promise<void> {
  const requestId = ++stockOptionsRequestId
  stockOptionsLoading.value = true
  stockOptionsStatus.value = 'loading'
  const request = (async () => {
    try {
      const options = await api.stocks.options({ includeClosed: true })
      if (requestId !== stockOptionsRequestId) return
      stockOptions.value = options
      stockOptionsLoaded.value = true
      stockOptionsStatus.value = 'ready'
    } catch (error) {
      if (requestId !== stockOptionsRequestId) return
      stockOptionsLoaded.value = false
      stockOptionsStatus.value = 'error'
      stockOptions.value = stocks.value.map(toStockOption)
      toast.error(error instanceof Error ? error.message : errorMessage)
    } finally {
      if (requestId === stockOptionsRequestId)
        stockOptionsLoading.value = false
    }
  })()
  stockOptionsRequest = request
  try {
    await request
  } finally {
    if (stockOptionsRequest === request)
      stockOptionsRequest = null
  }
}

// Ledger 或 Stock mutation 後強制重新讀取 options，確保快取不會保留刪除或舊部位。
async function refreshStockOptions(): Promise<void> {
  invalidateStockOptions()
  return requestStockOptions('交易已儲存，但更新交易股票清單失敗')
}

// 新增 position 後使完整 options 失效，避免新股票被舊 cache 遮蔽。
function invalidateStockOptions(): void {
  stockOptionsRequestId += 1
  stockOptionsRequest = null
  stockOptionsLoading.value = false
  stockOptionsStatus.value = 'idle'
  stockOptionsLoaded.value = false
  stockOptions.value = stocks.value.map(toStockOption)
}

// 只在交易紀錄 tab 啟用時載入 Ledger，避免股票頁初始查詢被非必要資料拖慢。
async function fetchLedger() {
  ledgerLoading.value = true
  try {
    const result = await api.stocks.ledger.list({
      stockId: ledgerStockId.value ?? undefined,
      type: ledgerType.value || undefined,
      dateStart: ledgerDateStart.value || undefined,
      dateEnd: ledgerDateEnd.value || undefined,
      page: ledgerPage.value,
      pageSize: ledgerPageSize,
    })
    ledgerItems.value = result.items
    ledgerTotal.value = result.total
  } catch (error) {
    toast.error(error instanceof Error ? error.message : '載入交易紀錄失敗')
  } finally {
    ledgerLoading.value = false
  }
}

// 送出 atomic Ledger initialization，成功後重新整理持股 projection 與初始化狀態。
async function initializeLedger(baselineDate: string): Promise<void> {
  initializationLoading.value = true
  try {
    initializationResponse.value = await api.stocks.ledger.initialize({ baselineDate })
    await Promise.all([fetchStocks(), refreshStockOptions()])
    toast.success(initializationResponse.value.blockingCount > 0 ? '部分持股需要先補正資料' : 'Ledger 初始化完成')
  } catch (error) {
    toast.error(error instanceof Error ? error.message : 'Ledger 初始化失敗')
  } finally {
    initializationLoading.value = false
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

watch(includeClosed, () => {
  pagination.page.value = 1
  void fetchStocks()
})

watch(activeTab, (tab) => {
  if (tab === 'ledger') void fetchLedger()
})

watch(ledgerPage, () => {
  if (activeTab.value === 'ledger') void fetchLedger()
})

watch([ledgerStockId, ledgerType, ledgerDateStart, ledgerDateEnd], () => {
  ledgerPage.value = 1
  if (activeTab.value === 'ledger') void fetchLedger()
})

// 先同步股票與分頁，待篩選 watcher 完成後再啟用 Ledger，確保導覽只查詢一次。
async function viewHoldingLedger(item: StockListItem): Promise<void> {
  ledgerStockId.value = item.id
  ledgerPage.value = 1
  await nextTick()
  activeTab.value = 'ledger'
}

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

/** 建立股票更新契約允許的最小 metadata payload。 */
function buildStockMetadataUpdatePayload(
  formState: StockFormState,
  currentPrice: number,
  lastPriceUpdate: string | null,
): Pick<Stock, 'name' | 'market' | 'currentPrice' | 'lastPriceUpdate'> {
  return {
    name: formState.name.trim(),
    market: formState.market,
    currentPrice,
    lastPriceUpdate,
  }
}

/** 開啟新增表單前使前一個表單的 symbol lookup 失效。 */
function openCreate() {
  invalidatePendingStockLookup()
  editingItem.value = null
  form.value = createEmptyStockForm()
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
    tradeDate: timeZone.getToday(),
    initialTransactionType: 'Buy',
  }
  syncPrice.value = true
  marketDirty.value = true
  modalOpen.value = true
}

/** 依目前頁面脈絡解析新增交易的預設股票，且不修改任何篩選或 options state。 */
function resolveDefaultTransactionStockId(): number | undefined {
  if (activeTab.value === 'ledger' && ledgerStockId.value !== null)
    return ledgerStockId.value
  return stockOptions.value[0]?.id ?? stocks.value[0]?.id
}

/** 依明確股票、Ledger 篩選與 fallback 開啟新增交易表單，預設型別維持 Buy。 */
function openTransaction(
  stockId?: number,
  type: EditableStockTransactionType = 'Buy',
): void {
  const resolvedStockId = stockId ?? resolveDefaultTransactionStockId()
  if (!resolvedStockId) return
  transactionEditing.value = null
  transactionError.value = ''
  transactionStockId.value = resolvedStockId
  transactionInitialType.value = type
  transactionModalOpen.value = true
  void loadStockOptions()
}

// 以選取的 Ledger row 開啟編輯表單，保持交易 id 與 replay row 的對應。
function editTransaction(item: StockTransactionListItem): void {
  transactionEditing.value = item
  transactionError.value = ''
  transactionStockId.value = item.stockId
  transactionInitialType.value = item.type === 'OpeningBalance' ? 'Buy' : item.type
  transactionModalOpen.value = true
  void loadStockOptions()
}

// 開啟交易刪除確認，避免在 replay 歷史上誤刪資料。
function confirmDeleteTransaction(id: number): void {
  deletingTransactionId.value = id
  transactionConfirmOpen.value = true
}

// 透過 Ledger delete command 刪除交易，成功後同步重新載入 projection。
async function deleteTransaction(): Promise<void> {
  if (deletingTransactionId.value === null) return
  try {
    await api.stocks.ledger.delete(deletingTransactionId.value)
    transactionConfirmOpen.value = false
    deletingTransactionId.value = null
    toast.success('交易已刪除')
    await Promise.all([fetchLedger(), fetchStocks(), refreshStockOptions()])
  } catch (error) {
    toast.error(error instanceof Error ? error.message : '刪除交易失敗')
  }
}

// 建立或修改交易，成功後重新載入 Ledger rows 與持股 projection。
async function saveTransaction(request: StockLedgerTransactionRequest): Promise<void> {
  transactionSaving.value = true
  transactionError.value = ''
  let mutationSucceeded = false
  try {
    if (transactionEditing.value) {
      await api.stocks.ledger.update(transactionEditing.value.id, request)
      toast.success('交易已更新')
    } else {
      await api.stocks.ledger.create(request)
      toast.success('交易已建立')
    }
    mutationSucceeded = true
    transactionModalOpen.value = false
    transactionEditing.value = null
  } catch (error) {
    transactionError.value = error instanceof Error ? error.message : '儲存交易失敗'
    toast.error(transactionError.value)
  } finally {
    transactionSaving.value = false
  }

  if (mutationSucceeded) {
    try {
      await Promise.all([fetchLedger(), fetchStocks(), refreshStockOptions()])
    } catch {
      toast.error('交易已儲存，但重新整理資料失敗')
    }
  }
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

      const updatePayload = buildStockMetadataUpdatePayload(
        formSnapshot,
        priceSyncResult.currentPrice,
        priceSyncResult.lastPriceUpdate,
      )
      await api.stocks.update(editingSnapshot.id, updatePayload)
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
      await api.stocks.positions.create({
        name: payloadSnapshot.name,
        symbol: payloadSnapshot.symbol,
        market: payloadSnapshot.market,
        instrumentType: payloadSnapshot.instrumentType,
        shares: payloadSnapshot.shares,
        buyPrice: payloadSnapshot.buyPrice,
        currentPrice: payloadSnapshot.currentPrice,
        tradeDate: formSnapshot.tradeDate,
        initialTransactionType: formSnapshot.initialTransactionType,
        broker: payloadSnapshot.broker,
        openingMarketValue: formSnapshot.initialTransactionType === 'OpeningBalance'
          ? formSnapshot.shares * formSnapshot.currentPrice
          : null,
      })
      if (editingItem.value === editingSnapshot && form.value === formIdentity)
        modalOpen.value = false
      toast.success('股票已建立')
      invalidateStockOptions()
      mutationSucceeded = true
    }
  } catch (e) {
    toast.error(e instanceof Error ? e.message : '儲存失敗')
  } finally {
    saving.value = false
  }

  if (mutationSucceeded) {
    try {
      await Promise.all([fetchStocks(), refreshStockOptions()])
    } catch {
      toast.error('股票已儲存，但重新整理清單失敗')
    }
  }
}

// 開啟 legacy 股票刪除確認，Ledger-managed 股票不會走到此流程。
function confirmDelete(id: number) {
  deletingId.value = id
  confirmOpen.value = true
}

// 刪除 legacy 股票後同步更新持股列表與交易 options。
async function doDelete() {
  if (deletingId.value !== null) {
    try {
      await api.stocks.delete(deletingId.value)
      confirmOpen.value = false
      deletingId.value = null
      toast.success('股票已刪除')
      await Promise.all([fetchStocks(), refreshStockOptions()])
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
        <Button v-if="stocks.length > 0" data-testid="new-stock-transaction" variant="ghost" @click="openTransaction()">+ 新增交易</Button>
        <Button @click="openCreate">+ 新增股票</Button>
      </div>
    </div>

    <div data-testid="stock-tabs" role="tablist" aria-label="股票資料類型" class="mb-6 flex max-w-full gap-1 overflow-x-auto whitespace-nowrap border-b border-border-default">
      <button
        data-testid="stock-tab-holdings"
        type="button"
        role="tab"
        :aria-selected="activeTab === 'holdings'"
        class="shrink-0 border-b-2 px-4 py-2.5 text-sm font-medium transition-colors cursor-pointer"
        :class="activeTab === 'holdings' ? 'border-accent-primary text-accent-primary' : 'border-transparent text-text-secondary hover:text-text-primary'"
        @click="activeTab = 'holdings'"
      >持股</button>
      <button
        data-testid="stock-tab-ledger"
        type="button"
        role="tab"
        :aria-selected="activeTab === 'ledger'"
        class="shrink-0 border-b-2 px-4 py-2.5 text-sm font-medium transition-colors cursor-pointer"
        :class="activeTab === 'ledger' ? 'border-accent-primary text-accent-primary' : 'border-transparent text-text-secondary hover:text-text-primary'"
        @click="activeTab = 'ledger'"
      >交易紀錄</button>
      <button
        data-testid="stock-closed-toggle"
        type="button"
        role="switch"
        :aria-pressed="includeClosed"
        class="ml-auto shrink-0 rounded-lg px-3 py-1.5 text-xs transition-colors cursor-pointer"
        :class="includeClosed ? 'bg-bg-active text-text-primary' : 'text-text-secondary hover:bg-bg-raised'"
        @click="includeClosed = !includeClosed"
      >{{ includeClosed ? '隱藏已結清' : '顯示已結清' }}</button>
    </div>

    <div v-if="activeTab === 'holdings'">
      <StockLedgerInitialization
        :has-active-holdings="hasUninitializedActiveHoldings"
        :loading="initializationLoading"
        :response="initializationResponse"
        :holdings="stocks"
        @initialize="initializeLedger"
      />

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
      <StockHoldingsTable
        :items="stocks"
        :loading="loading"
        :page="pagination.page.value"
        :page-size="pagination.pageSize.value"
        @view-ledger="viewHoldingLedger"
        @edit="openEdit"
        @buy="item => openTransaction(item.id, 'Buy')"
        @sell="item => openTransaction(item.id, 'Sell')"
        @delete="confirmDelete"
      />
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
    </div>

    <div v-else data-testid="stock-ledger-panel">
      <Card class="mb-4">
        <div class="grid grid-cols-1 gap-3 md:grid-cols-4">
          <div>
            <label for="ledger-stock-filter" class="mb-1 block text-xs font-medium text-text-secondary">股票</label>
            <select
              id="ledger-stock-filter"
              v-model="ledgerStockId"
              data-testid="ledger-stock-filter"
              class="min-h-11 w-full rounded-lg border border-border-strong bg-bg-card px-3 py-2 text-sm text-text-primary focus:border-accent-primary focus:outline-none focus:ring-2 focus:ring-focus-ring"
            >
              <option :value="null">全部股票</option>
              <option v-for="stock in stocks" :key="stock.id" :value="stock.id">{{ stock.symbol }} {{ stock.name }}</option>
            </select>
          </div>
          <div>
            <label for="ledger-type-filter" class="mb-1 block text-xs font-medium text-text-secondary">交易類型</label>
            <select
              id="ledger-type-filter"
              v-model="ledgerType"
              data-testid="ledger-type-filter"
              class="min-h-11 w-full rounded-lg border border-border-strong bg-bg-card px-3 py-2 text-sm text-text-primary focus:border-accent-primary focus:outline-none focus:ring-2 focus:ring-focus-ring"
            >
              <option value="">全部類型</option>
              <option value="OpeningBalance">期初部位</option>
              <option value="Buy">買入</option>
              <option value="Sell">賣出</option>
              <option value="Dividend">現金股利</option>
              <option value="StockDividend">股票股利／配股</option>
            </select>
          </div>
          <div>
            <label for="ledger-date-start" class="mb-1 block text-xs font-medium text-text-secondary">起始日期</label>
            <input id="ledger-date-start" v-model="ledgerDateStart" data-testid="ledger-date-start" type="date" class="min-h-11 w-full rounded-lg border border-border-strong bg-bg-card px-3 py-2 text-sm text-text-primary focus:border-accent-primary focus:outline-none focus:ring-2 focus:ring-focus-ring" />
          </div>
          <div>
            <label for="ledger-date-end" class="mb-1 block text-xs font-medium text-text-secondary">結束日期</label>
            <input id="ledger-date-end" v-model="ledgerDateEnd" data-testid="ledger-date-end" type="date" class="min-h-11 w-full rounded-lg border border-border-strong bg-bg-card px-3 py-2 text-sm text-text-primary focus:border-accent-primary focus:outline-none focus:ring-2 focus:ring-focus-ring" />
          </div>
        </div>
      </Card>
      <StockTransactionLedger
        :items="ledgerItems"
        :loading="ledgerLoading"
        :total="ledgerTotal"
        :has-stocks="stocks.length > 0"
        :page="ledgerPage"
        :page-size="ledgerPageSize"
        @create="openTransaction()"
        @edit="editTransaction"
        @delete="confirmDeleteTransaction"
        @previous="ledgerPage = Math.max(1, ledgerPage - 1)"
        @next="ledgerPage += 1"
      />
    </div>

    <Modal :open="modalOpen" :title="editingItem ? '編輯股票' : '新增股票'" :close-disabled="saving" @update:open="modalOpen = $event">
      <form @submit.prevent="save">
        <fieldset :disabled="saving" class="m-0 min-w-0 space-y-4 border-0 p-0">
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">代號</label>
          <Input v-model="form.symbol" placeholder="e.g. 2330" :disabled="!!editingItem" />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">名稱</label>
          <Input v-model="form.name" :error="formErrors.name" placeholder="e.g. 台積電" @update:model-value="lookupDirty.name = true" />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">交易市場</label>
          <select
            v-model="form.market"
            data-testid="stock-edit-market"
            :disabled="ledgerMarketLocked"
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
            data-testid="stock-edit-instrument-type"
            :disabled="!!editingItem"
            class="w-full px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
          >
            <option v-for="option in STOCK_INSTRUMENT_TYPE_OPTIONS" :key="option.value" :value="option.value">
              {{ option.label }}
            </option>
          </select>
        </div>
        <template v-if="!editingItem">
          <div>
            <label class="block text-sm font-medium text-text-primary mb-1">初始部位來源</label>
            <select
              v-model="form.initialTransactionType"
              data-testid="stock-initial-transaction-type"
              class="w-full px-3 py-2 border border-border-strong rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary"
            >
              <option value="Buy">新買入</option>
              <option value="OpeningBalance">既有部位帶入</option>
            </select>
            <p class="mt-1 text-xs text-text-secondary">股票與第一筆 Ledger 會以單一 atomic request 建立。</p>
          </div>
          <div>
            <label class="block text-sm font-medium text-text-primary mb-1">初始交易日期</label>
            <Input v-model="form.tradeDate" data-testid="stock-initial-trade-date" type="date" :error="formErrors.tradeDate" />
          </div>
        </template>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">股數</label>
          <Input
            :model-value="form.shares || ''"
            type="number"
            step="1"
            :disabled="!!editingItem"
            :error="formErrors.shares"
            @update:model-value="form.shares = Number($event) || 0"
          />
          <p v-if="editingLedgerManaged" class="mt-1 text-xs text-text-secondary">股數由交易紀錄管理</p>
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">買入均價</label>
          <Input
            :model-value="form.buyPrice || ''"
            type="number"
            step="0.01"
            :disabled="!!editingItem"
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
            :error="formErrors.currentPrice"
            @update:model-value="lookupDirty.currentPrice = true; form.currentPrice = Number($event) || 0"
          />
        </div>
        <div>
          <label class="block text-sm font-medium text-text-primary mb-1">券商</label>
          <Input v-model="form.broker" placeholder="e.g. 元大證券" :disabled="!!editingItem" />
        </div>
        <p v-if="editingLedgerManaged" class="text-xs text-text-secondary">
          此持股已有交易紀錄。股數、均價與股票識別欄位由 Ledger 管理；如需改變部位，請新增或修改交易。
        </p>
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

    <StockTransactionModal
      :open="transactionModalOpen"
      :stocks="stockOptions"
      :stock-id="transactionStockId"
      :transaction="transactionEditing"
      :initial-type="transactionInitialType"
      :stock-options-status="stockOptionsStatus"
      :stock-identity-fallback="transactionStockIdentity"
      :loading="transactionSaving"
      :error-message="transactionError"
      @update:open="transactionModalOpen = $event"
      @save="saveTransaction"
    />

    <ConfirmDialog
      :open="confirmOpen"
      title="刪除股票"
      description="確定要刪除此股票記錄嗎？此操作無法復原。"
      variant="danger"
      @update:open="confirmOpen = $event"
      @confirm="doDelete"
    />

    <ConfirmDialog
      :open="transactionConfirmOpen"
      title="刪除交易"
      description="確定要刪除此筆交易嗎？系統會重新 replay 後續歷史。"
      variant="danger"
      @update:open="transactionConfirmOpen = $event"
      @confirm="deleteTransaction"
    />
  </div>
</template>
