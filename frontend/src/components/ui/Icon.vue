<script setup lang="ts">
import { computed } from 'vue'
import { iconRegistry, normalizeIconName, resolveIcon } from './icon-registry'

const props = withDefaults(defineProps<{
  name: string
  size?: number
  color?: string
  strokeWidth?: number
}>(), {
  size: 20,
  strokeWidth: 2,
})

// 解析圖示並保留開發環境中對未知名稱的診斷訊息。
const iconComponent = computed(() => {
  const iconName = normalizeIconName(props.name)
  if (!iconRegistry[iconName as keyof typeof iconRegistry]) {
    console.warn(`Icon "${iconName}" not found in lucide`)
  }
  return resolveIcon(props.name)
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
