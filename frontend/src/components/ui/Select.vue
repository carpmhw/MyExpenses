<script setup lang="ts">
import { computed, useAttrs } from 'vue'

defineOptions({ inheritAttrs: false })

const props = defineProps<{
  modelValue?: string | number
  id?: string
  options: { value: string | number; label: string }[]
  placeholder?: string
  error?: string
  disabled?: boolean
}>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
  blur: [event: FocusEvent]
}>()

const attrs = useAttrs()
const errorId = computed(() => props.id && props.error ? `${props.id}-error` : undefined)
const describedBy = computed(() => {
  const values = [attrs['aria-describedby'], errorId.value].filter(Boolean)
  return values.length > 0 ? values.join(' ') : undefined
})

// 將原生選項值轉回表單擁有者。
function onChange(event: Event) {
  emit('update:modelValue', (event.target as HTMLSelectElement).value)
}

// 將選單離焦事件傳回表單以觸發延遲驗證。
function onBlur(event: FocusEvent) {
  emit('blur', event)
}
</script>

<template>
  <select
    v-bind="attrs"
    :value="modelValue"
    :id="id"
    :disabled="disabled"
    :aria-invalid="error ? 'true' : undefined"
    :aria-describedby="describedBy"
    @change="onChange"
    @blur="onBlur"
    :class="[
      'w-full min-h-11 px-3 py-2 border rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-focus-ring focus:border-accent-primary',
      error ? 'border-color-expense-text focus:border-color-expense-text' : 'border-border-strong',
      disabled ? 'opacity-50 cursor-not-allowed' : '',
    ]"
  >
    <option v-if="placeholder" value="">{{ placeholder }}</option>
    <option v-for="opt in options" :key="opt.value" :value="opt.value">{{ opt.label }}</option>
  </select>
  <p v-if="error" :id="errorId" class="mt-1 text-xs text-color-expense-text">{{ error }}</p>
</template>
