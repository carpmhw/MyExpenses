<script setup lang="ts">
import { computed, ref, useAttrs } from 'vue'

defineOptions({ inheritAttrs: false })

const props = withDefaults(defineProps<{
  modelValue?: string | number
  id?: string
  placeholder?: string
  type?: string
  error?: string
  step?: string
  min?: number
  max?: number
  maxlength?: number
  disabled?: boolean
}>(), {
  type: 'text',
  disabled: false,
})

const emit = defineEmits<{
  'update:modelValue': [value: string]
  blur: [event: FocusEvent]
}>()

const attrs = useAttrs()
const inputElement = ref<HTMLInputElement | null>(null)
const errorId = computed(() => props.id && props.error ? `${props.id}-error` : undefined)
const describedBy = computed(() => {
  const values = [attrs['aria-describedby'], errorId.value].filter(Boolean)
  return values.length > 0 ? values.join(' ') : undefined
})

// 將原生輸入值傳回表單擁有者。
function onInput(e: Event) {
  const target = e.target as HTMLInputElement
  emit('update:modelValue', target.value)
}

// 將欄位離焦事件傳回表單以觸發延遲驗證。
function onBlur(e: FocusEvent) {
  emit('blur', e)
}

// 將焦點方法暴露給需要控制初始焦點的表單元件。
function focus(): void {
  inputElement.value?.focus()
}

defineExpose({ focus })
</script>

<template>
  <div>
    <input
      ref="inputElement"
      v-bind="attrs"
      :type="type"
      :id="id"
      :placeholder="placeholder"
      :value="modelValue"
      :step="step"
      :min="min"
      :max="max"
      :maxlength="maxlength"
      :disabled="disabled"
      :aria-invalid="error ? 'true' : undefined"
      :aria-describedby="describedBy"
      @input="onInput"
      @blur="onBlur"
      :class="[
        'w-full min-h-11 px-3 py-2 border rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring placeholder:text-text-tertiary transition-colors',
        error ? 'border-color-expense-text focus:border-color-expense-text' : 'border-border-strong focus:border-accent-primary',
        disabled ? 'opacity-50 cursor-not-allowed' : '',
      ]"
    >
    <p v-if="error" :id="errorId" class="mt-1 text-xs text-color-expense-text">{{ error }}</p>
  </div>
</template>
