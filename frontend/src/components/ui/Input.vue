<script setup lang="ts">
const props = withDefaults(defineProps<{
  modelValue?: string | number
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
}>()

function onInput(e: Event) {
  const target = e.target as HTMLInputElement
  emit('update:modelValue', target.value)
}
</script>

<template>
  <div>
    <input
      :type="type"
      :placeholder="placeholder"
      :value="modelValue"
      :step="step"
      :min="min"
      :max="max"
      :maxlength="maxlength"
      :disabled="disabled"
      @input="onInput"
      :class="[
        'w-full px-3 py-2 border rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30 placeholder:text-text-tertiary transition-colors',
        error ? 'border-red-400 focus:border-red-500' : 'border-border-default focus:border-accent-primary',
        disabled ? 'opacity-50 cursor-not-allowed' : '',
      ]"
    >
    <p v-if="error" class="mt-1 text-xs text-red-500">{{ error }}</p>
  </div>
</template>
