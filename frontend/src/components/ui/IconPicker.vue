<script setup lang="ts">
import { ref, computed } from 'vue'
import Modal from './Modal.vue'
import Icon from './Icon.vue'

const props = withDefaults(defineProps<{
  modelValue?: string
  error?: string
}>(), {})

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()

const open = ref(false)
const search = ref('')
const selected = ref(props.modelValue ?? '')

const icons = [
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

const filteredIcons = computed(() => {
  const q = search.value.toLowerCase().trim()
  if (!q) return icons
  return icons.filter(name => name.toLowerCase().includes(q))
})

function selectIcon(name: string) {
  selected.value = name
  emit('update:modelValue', name)
  open.value = false
  search.value = ''
}

function openPicker() {
  selected.value = props.modelValue ?? ''
  search.value = ''
  open.value = true
}
</script>

<template>
  <div>
    <div
      class="flex items-center gap-3 px-3 py-2 border rounded-lg cursor-pointer transition-colors bg-bg-card"
      :class="error ? 'border-red-400' : 'border-border-default hover:border-accent-primary'"
      @click="openPicker"
    >
      <Icon v-if="modelValue" :name="modelValue" :size="20" />
      <span v-if="modelValue" class="text-sm text-text-primary">{{ modelValue }}</span>
      <span v-else class="text-sm text-text-tertiary">點擊選擇圖示</span>
      <span class="ml-auto text-xs text-text-tertiary">瀏覽</span>
    </div>
    <p v-if="error" class="mt-1 text-xs text-red-500">{{ error }}</p>

    <Modal :open="open" title="選擇圖示" size="lg" @update:open="open = $event">
      <div class="space-y-4">
        <input
          v-model="search"
          placeholder="搜尋圖示..."
          class="w-full px-3 py-2 border border-border-default rounded-lg text-sm text-text-primary bg-bg-card focus:outline-none focus:ring-2 focus:ring-accent-primary/30"
        >

        <div class="max-h-80 overflow-y-auto">
          <div v-if="filteredIcons.length === 0" class="text-center text-text-tertiary py-8 text-sm">
            找不到符合的圖示
          </div>
          <div v-else class="grid grid-cols-8 gap-2">
            <button
              v-for="name in filteredIcons"
              :key="name"
              :class="[
                'flex flex-col items-center gap-1 p-2 rounded-lg text-xs transition-colors cursor-pointer',
                modelValue === name
                  ? 'bg-accent-primary/10 ring-2 ring-accent-primary'
                  : 'hover:bg-gray-100 dark:hover:bg-gray-700',
              ]"
              @click="selectIcon(name)"
            >
              <Icon :name="name" :size="24" />
              <span class="text-text-secondary truncate w-full text-center">{{ name }}</span>
            </button>
          </div>
        </div>
      </div>
    </Modal>
  </div>
</template>
