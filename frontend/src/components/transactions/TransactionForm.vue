<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref, watch } from 'vue'
import type { Category, CreditCard, PaymentMethod, Transaction } from '../../types'
import Button from '../ui/Button.vue'
import Input from '../ui/Input.vue'
import Select from '../ui/Select.vue'
import {
  buildTransactionCommand,
  cloneTransactionForm,
  getTransactionActionLabel,
  isCreditCardPaymentMethod,
  normalizeTransactionForm,
  type TransactionFormCommand,
  type TransactionFormErrors,
  type TransactionFormOptions,
  type TransactionFormValues,
  type TransactionPaymentMode,
  type TransactionType,
} from '../../utils/transactionForm'
import { formatMoney } from '../../utils/format'

const props = withDefaults(defineProps<{
  initialValue: TransactionFormValues
  categories: Category[]
  paymentMethods: PaymentMethod[]
  creditCards: CreditCard[]
  editing?: Transaction | null
  submitting?: boolean
  disabled?: boolean
  referenceDataReady?: boolean
  referenceDataError?: string | null
  creditCardDataReady?: boolean
  creditCardDataError?: string | null
  submissionError?: string | null
  submissionNotice?: string | null
  submissionUncertain?: boolean
  submissionRetryAllowed?: boolean
}>(), {
  editing: null,
  submitting: false,
  disabled: false,
  referenceDataReady: true,
  referenceDataError: null,
  creditCardDataReady: true,
  creditCardDataError: null,
  submissionError: null,
  submissionNotice: null,
  submissionUncertain: false,
  submissionRetryAllowed: false,
})

const emit = defineEmits<{
  submit: [command: TransactionFormCommand]
  cancel: []
  retryReferenceData: []
  refreshTransactions: []
}>()

const form = reactive<TransactionFormValues>(cloneTransactionForm(props.initialValue))
const touched = reactive<Record<string, boolean>>({})
const submitted = ref(false)
const dateInput = ref<{ focus: () => void } | null>(null)
const errorSummary = ref<HTMLElement | null>(null)
const submissionErrorElement = ref<HTMLElement | null>(null)
const submissionNoticeElement = ref<HTMLElement | null>(null)

const context = computed<TransactionFormOptions>(() => ({
  categories: props.categories,
  paymentMethods: props.paymentMethods,
  creditCards: props.creditCards,
  editing: props.editing,
}))

const allErrors = computed<TransactionFormErrors>(() => buildTransactionCommand(form, context.value).errors)
const displayedErrors = computed<TransactionFormErrors>(() => {
  if (submitted.value) return allErrors.value
  return Object.fromEntries(Object.entries(allErrors.value).filter(([field]) => touched[field]))
})
const categoryOptions = computed(() => props.categories
  .filter(category => category.type === form.type)
  .map(category => ({ value: category.id, label: category.name })))
const paymentMethodOptions = computed(() => props.paymentMethods
  .filter(paymentMethod => form.type !== 'Income' || !isCreditCardPaymentMethod(paymentMethod))
  .map(paymentMethod => ({ value: paymentMethod.id, label: paymentMethod.name })))
const creditCardOptions = computed(() => props.creditCards.map(card => ({
  value: card.id,
  label: `${card.bankName} (${card.lastFourDigits})`,
})))
const selectedPaymentMethod = computed(() => props.paymentMethods.find(item => item.id === form.paymentMethodId))
const isCreditCardSelected = computed(() => isCreditCardPaymentMethod(selectedPaymentMethod.value))
const showPaymentPath = computed(() => !props.editing && form.type === 'Expense' && isCreditCardSelected.value)
const showInstallmentDetails = computed(() => showPaymentPath.value && form.paymentMode === 'installment')
const referenceDataReady = computed(() => props.referenceDataReady && (!showInstallmentDetails.value || props.creditCardDataReady))
const referenceError = computed(() => props.referenceDataError || (showInstallmentDetails.value ? props.creditCardDataError : null))
const fieldsDisabled = computed(() => props.submitting || props.submissionUncertain)
const canSubmit = computed(() => !props.submitting && !props.disabled && referenceDataReady.value && !referenceError.value && (!props.submissionUncertain || props.submissionRetryAllowed))
const actionLabel = computed(() => props.submissionUncertain && props.submissionRetryAllowed ? '使用相同資料重試' : getTransactionActionLabel(form, Boolean(props.editing)))
const commandPreview = computed(() => {
  const result = buildTransactionCommand(form, context.value)
  if (!result.command) return null
  if (result.command.kind === 'purchase') {
    return `將於 ${form.date} 建立 ${formatMoney(form.amount)} 支出與 ${form.installmentPeriods} 期付款時程。`
  }
  if (result.command.kind === 'update') return `將更新 ${form.date} 的 ${formatMoney(form.amount)} 交易。`
  return `將於 ${form.date} 建立 ${form.type === 'Income' ? '收入' : '支出'} ${formatMoney(form.amount)}。`
})

// 將目前表單狀態同步到仍然相容的網域值。
function synchronizeDomainState(): void {
  const normalized = normalizeTransactionForm(form, context.value)
  Object.assign(form, normalized)
}

// 根據表單互動狀態回傳應該顯示的欄位錯誤。
function fieldError(field: string): string | undefined {
  return displayedErrors.value[field]
}

// 記錄欄位已被使用者操作，以延遲顯示驗證訊息。
function touch(field: string): void {
  touched[field] = true
}

// 將選單字串安全轉成交易類型網域值。
function setTransactionType(value: string): void {
  form.type = value as TransactionType
}

// 將選單字串安全轉成信用卡付款模式網域值。
function setPaymentMode(value: string): void {
  form.paymentMode = value as TransactionPaymentMode
}

// 將焦點移到錯誤摘要，讓鍵盤與輔助技術立即知道送出失敗原因。
function focusErrorSummary(): void {
  nextTick(() => errorSummary.value?.focus())
}

// 開啟表單後將初始焦點放在日期欄位。
function focusDate(): void {
  nextTick(() => dateInput.value?.focus())
}

// 將伺服器結果焦點移到表單內的可讀回饋區塊。
function focusSubmissionFeedback(): void {
  nextTick(() => (submissionErrorElement.value ?? submissionNoticeElement.value)?.focus())
}

// 處理表單送出並只發出已通過網域驗證的命令。
function handleSubmit(): void {
  submitted.value = true
  synchronizeDomainState()
  const result = buildTransactionCommand(form, context.value)
  if (!referenceDataReady.value || referenceError.value || Object.keys(result.errors).length > 0 || !result.command) {
    focusErrorSummary()
    return
  }
  emit('submit', result.command)
}

// 以新的父層初始值重置表單與延遲驗證狀態。
function resetFromProps(values: TransactionFormValues): void {
  Object.assign(form, cloneTransactionForm(values))
  Object.keys(touched).forEach(field => delete touched[field])
  submitted.value = false
  synchronizeDomainState()
  focusDate()
}

watch(
  () => [form.type, form.categoryId, form.paymentMethodId, form.paymentMode, form.installmentCardId, form.installmentPeriods],
  synchronizeDomainState,
  { flush: 'sync' },
)

watch(() => props.initialValue, resetFromProps, { deep: true })
watch(
  () => [props.submissionError, props.submissionNotice],
  ([error, notice], previous) => {
    if ((error && error !== previous[0]) || (notice && notice !== previous[1])) focusSubmissionFeedback()
  },
)

onMounted(focusDate)
</script>

<template>
  <form class="space-y-4" novalidate @submit.prevent="handleSubmit">
    <div
      v-if="submitted && (Object.keys(displayedErrors).length > 0 || !referenceDataReady || referenceError)"
      ref="errorSummary"
      tabindex="-1"
      role="alert"
      class="rounded-lg border border-color-expense-text/40 bg-color-expense-bg px-3 py-2 text-sm text-color-expense-text focus:outline-none focus:ring-2 focus:ring-focus-ring"
    >
      請修正表單中的錯誤後再送出。
    </div>

    <div v-if="referenceError" role="alert" class="flex items-center justify-between gap-3 rounded-lg border border-color-warning-text/40 bg-color-warning-bg px-3 py-2 text-sm text-color-warning-text">
      <span>{{ referenceError }}</span>
      <Button type="button" variant="ghost" @click="emit('retryReferenceData')">重試</Button>
    </div>

    <div v-if="submissionError" ref="submissionErrorElement" role="alert" tabindex="-1" class="rounded-lg border border-color-expense-text/40 bg-color-expense-bg px-3 py-2 text-sm text-color-expense-text focus:outline-none focus:ring-2 focus:ring-focus-ring">
      {{ submissionError }}
    </div>
    <div v-if="submissionNotice" ref="submissionNoticeElement" role="status" aria-live="polite" tabindex="-1" class="flex items-center justify-between gap-3 rounded-lg border border-color-warning-text/40 bg-color-warning-bg px-3 py-2 text-sm text-color-warning-text focus:outline-none focus:ring-2 focus:ring-focus-ring">
      <span>{{ submissionNotice }}</span>
      <Button type="button" variant="ghost" @click="emit('refreshTransactions')">重新整理交易列表</Button>
    </div>

    <div>
      <label for="transaction-date" class="block text-sm font-medium text-text-primary mb-1">交易日期</label>
      <Input
        id="transaction-date"
        ref="dateInput"
        v-model="form.date"
        type="date"
        :disabled="fieldsDisabled"
        :error="fieldError('date')"
        @blur="touch('date')"
      />
    </div>

    <div>
      <label for="transaction-type" class="block text-sm font-medium text-text-primary mb-1">交易類型</label>
      <Select
        id="transaction-type"
        :model-value="form.type"
        :options="[{ value: 'Expense', label: '支出' }, { value: 'Income', label: '收入' }]"
        :disabled="fieldsDisabled"
        @update:model-value="setTransactionType"
        @blur="touch('type')"
      />
    </div>

    <div class="grid grid-cols-1 gap-4 sm:grid-cols-2">
      <div>
        <label for="transaction-amount" class="block text-sm font-medium text-text-primary mb-1">金額</label>
        <Input
          id="transaction-amount"
          :model-value="form.amount || ''"
          type="number"
          inputmode="decimal"
          step="0.01"
          :min="0"
          :disabled="fieldsDisabled"
          :error="fieldError('amount')"
          @update:model-value="form.amount = Number($event) || 0"
          @blur="touch('amount')"
        />
      </div>
      <div>
        <label for="transaction-category" class="block text-sm font-medium text-text-primary mb-1">類別</label>
        <Select
          id="transaction-category"
          :model-value="form.categoryId ?? ''"
          :options="categoryOptions"
          placeholder="選擇類別"
          :error="fieldError('categoryId')"
          :disabled="fieldsDisabled"
          @update:model-value="form.categoryId = Number($event) || null"
          @blur="touch('categoryId')"
        />
      </div>
    </div>

    <div>
      <label for="transaction-description" class="block text-sm font-medium text-text-primary mb-1">項目</label>
      <Input
        id="transaction-description"
        v-model="form.description"
        placeholder="例如：早餐店鐵板麵"
        :disabled="fieldsDisabled"
        :error="fieldError('description')"
        @blur="touch('description')"
      />
    </div>

    <div>
      <label for="transaction-payment-method" class="block text-sm font-medium text-text-primary mb-1">支付方式</label>
      <Select
        id="transaction-payment-method"
        :model-value="form.paymentMethodId ?? ''"
        :options="paymentMethodOptions"
        placeholder="選擇支付方式"
        :error="fieldError('paymentMethodId')"
        :disabled="fieldsDisabled || !referenceDataReady || Boolean(referenceError && !showInstallmentDetails)"
        @update:model-value="form.paymentMethodId = $event ? Number($event) : null"
        @blur="touch('paymentMethodId')"
      />
    </div>

    <template v-if="showPaymentPath">
      <div>
        <label for="transaction-payment-mode" class="block text-sm font-medium text-text-primary mb-1">信用卡付款方式</label>
        <Select
          id="transaction-payment-mode"
          :model-value="form.paymentMode"
          :options="[
            { value: 'one-time', label: '一次付清' },
            { value: 'installment', label: '分期付款' },
          ]"
          :disabled="fieldsDisabled"
          @update:model-value="setPaymentMode"
          @blur="touch('paymentMode')"
        />
      </div>

      <div v-if="showInstallmentDetails" class="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <div>
          <label for="transaction-installment-card" class="block text-sm font-medium text-text-primary mb-1">信用卡</label>
          <Select
            id="transaction-installment-card"
            :model-value="form.installmentCardId ?? ''"
            :options="creditCardOptions"
            placeholder="選擇信用卡"
            :error="fieldError('installmentCardId')"
            :disabled="fieldsDisabled || !props.creditCardDataReady"
            @update:model-value="form.installmentCardId = Number($event) || null"
            @blur="touch('installmentCardId')"
          />
        </div>
        <div>
          <label for="transaction-installment-periods" class="block text-sm font-medium text-text-primary mb-1">分期期數</label>
          <Input
            id="transaction-installment-periods"
            :model-value="form.installmentPeriods || ''"
            type="number"
            inputmode="numeric"
            :min="2"
            :error="fieldError('installmentPeriods')"
            :disabled="fieldsDisabled"
            @update:model-value="form.installmentPeriods = Number($event) || 0"
            @blur="touch('installmentPeriods')"
          />
        </div>
      </div>
    </template>

    <p v-if="props.editing && props.editing.paymentMethod?.systemCode === 'credit-card'" class="rounded-lg bg-bg-raised px-3 py-2 text-xs text-text-secondary">
      分期付款時程請至「分期管理」調整。
    </p>

    <div>
      <label for="transaction-notes" class="block text-sm font-medium text-text-primary mb-1">備註</label>
      <Input id="transaction-notes" v-model="form.notes" placeholder="備註說明" :disabled="fieldsDisabled" @blur="touch('notes')" />
    </div>

    <div v-if="commandPreview" role="status" aria-live="polite" class="rounded-lg border border-color-income-text/40 bg-color-income-bg px-3 py-2 text-sm text-color-income-text">
      {{ commandPreview }}
    </div>

    <div class="flex flex-col-reverse gap-3 pt-2 sm:flex-row sm:justify-end">
      <Button type="button" variant="ghost" class="min-h-11" :disabled="props.submitting" @click="emit('cancel')">取消</Button>
      <Button type="submit" class="min-h-11 w-full sm:w-auto" :loading="props.submitting" :disabled="!canSubmit">{{ actionLabel }}</Button>
    </div>
  </form>
</template>
