<script setup lang="ts">
import { computed } from 'vue'
import * as Icons from '@lucide/vue'

const props = withDefaults(defineProps<{
  name: string
  size?: number
  color?: string
  strokeWidth?: number
}>(), {
  size: 20,
  strokeWidth: 2,
})

const iconComponent = computed(() => {
  const iconName = props.name
    .replace(/-(\w)/g, (_, c) => c.toUpperCase())
    .replace(/^(\w)/, (_, c) => c.toUpperCase())

  const icon = (Icons as Record<string, unknown>)[iconName]
  if (!icon) {
    console.warn(`Icon "${iconName}" not found in lucide`)
    return null
  }
  return icon
})
</script>

<template>
  <component
    :is="iconComponent"
    v-if="iconComponent"
    :size="size"
    :color="color"
    :stroke-width="strokeWidth"
  />
</template>
