<script setup lang="ts">
import { inject } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import {
  LayoutDashboard,
  Banknote,
  Receipt,
  CreditCard,
  BarChart3,
  Tags,
  TrendingUp,
  Building2,
  Wallet,
  Camera,
  Settings,
  Moon,
  Sun,
} from '@lucide/vue'
import { useAuth } from '../composables/useAuth'

const props = defineProps<{
  isMobile?: boolean
  isTablet?: boolean
  isSidebarOpen?: boolean
}>()

const emit = defineEmits<{ close: [] }>()

const route = useRoute()
const router = useRouter()
const auth = useAuth()
const darkMode = inject<{ isDark: { value: boolean }; toggle: () => void }>('darkMode')!

const navItems = [
  { section: '主選單', items: [
    { label: '儀表板', icon: LayoutDashboard, route: '/dashboard' },
    { label: '提款紀錄', icon: Banknote, route: '/withdrawals' },
    { label: '交易明細', icon: Receipt, route: '/transactions' },
    { label: '信用卡分期', icon: CreditCard, route: '/installments' },
    { label: '報表分析', icon: BarChart3, route: '/reports' },
    { label: '財務快照', icon: Camera, route: '/snapshots' },
  ] },
  { section: '基本資料', items: [
    { label: '分類管理', icon: Tags, route: '/categories' },
    { label: '支付方式管理', icon: Wallet, route: '/payment-methods' },
    { label: '使用者設定', icon: Settings, route: '/settings' },
  ] },
  { section: '帳戶', items: [
    { label: '股票', icon: TrendingUp, route: '/stocks' },
    { label: '銀行帳戶', icon: Building2, route: '/bank-accounts' },
    { label: '信用卡', icon: CreditCard, route: '/credit-cards' },
  ] },
]

const isActive = (path: string) => route.path === path

function handleNavClick() {
  if (props.isMobile) {
    emit('close')
  }
}

function goToSettings() {
  if (props.isMobile) emit('close')
  router.push('/settings')
}
</script>

<template>
  <!-- Mobile overlay backdrop -->
  <div
    v-if="isMobile && isSidebarOpen"
    class="fixed inset-0 bg-black/50 z-40 lg:hidden"
    @click="emit('close')"
  />

  <!-- Sidebar -->
  <aside
    :class="[
      'h-screen bg-bg-sidebar flex flex-col shrink-0 transition-all duration-200',
      isMobile
        ? (isSidebarOpen ? 'fixed inset-y-0 left-0 z-50 w-64' : 'hidden')
        : isTablet
          ? 'w-16'
          : 'w-60',
    ]"
  >
    <!-- Brand -->
    <div class="flex items-center gap-3 px-3 py-6">
      <div class="w-9 h-9 shrink-0">
        <img src="/favicon.svg" alt="MyExpenses Logo" class="w-full h-full" />
      </div>
      <div v-if="!isTablet" class="flex flex-col">
        <span class="text-text-on-dark font-bold text-sm">MyExpenses</span>
        <span class="text-text-on-dark-muted text-xs">個人記帳</span>
      </div>
    </div>

    <!-- Navigation -->
    <nav class="flex-1 overflow-y-auto px-4">
      <template v-for="section in navItems" :key="section.section">
        <div v-if="!isTablet" class="pt-4 pb-2 px-1">
          <span class="text-text-on-dark-muted text-xs font-medium uppercase tracking-wider">{{ section.section }}</span>
        </div>
        <router-link
          v-for="item in section.items"
          :key="item.route"
          :to="item.route"
          :title="isTablet ? item.label : undefined"
          :class="[
            'flex items-center gap-3 px-3 py-2.5 rounded-lg text-sm transition-colors mb-1',
            isTablet ? 'justify-center' : '',
            isActive(item.route)
              ? 'bg-[#1E293B] text-text-on-dark'
              : 'text-text-on-dark-muted hover:text-text-on-dark hover:bg-white/5',
          ]"
          @click="handleNavClick"
        >
          <component :is="item.icon" class="w-[18px] h-[18px] shrink-0" :class="isActive(item.route) ? 'text-accent-primary' : ''" />
          <span v-if="!isTablet">{{ item.label }}</span>
        </router-link>
      </template>
    </nav>

    <!-- Footer: dark mode toggle + user card -->
    <div class="p-4 border-t border-white/10">
      <button
        class="flex items-center gap-3 w-full px-3 py-2.5 rounded-lg text-sm text-text-on-dark-muted hover:text-text-on-dark hover:bg-white/5 transition-colors cursor-pointer mb-2"
        :class="isTablet ? 'justify-center' : ''"
        @click="darkMode.toggle()"
      >
        <component :is="darkMode.isDark.value ? Sun : Moon" class="w-[18px] h-[18px] shrink-0" />
        <span v-if="!isTablet">{{ darkMode.isDark.value ? '淺色模式' : '深色模式' }}</span>
      </button>
      <button
        :class="['flex items-center gap-3 w-full px-3 py-2.5 rounded-lg text-sm transition-colors cursor-pointer hover:bg-white/5', isTablet ? 'justify-center' : '']"
        @click="goToSettings"
      >
        <div class="w-9 h-9 rounded-full bg-accent-primary flex items-center justify-center text-white font-semibold text-sm shrink-0">
          {{ (auth.user.value?.displayName || 'U')[0].toUpperCase() }}
        </div>
        <div v-if="!isTablet" class="flex flex-col text-left">
          <span class="text-text-on-dark text-sm font-medium">{{ auth.user.value?.displayName || '使用者' }}</span>
          <span class="text-text-on-dark-muted text-xs">{{ auth.user.value?.email || 'user@example.com' }}</span>
        </div>
      </button>
    </div>
  </aside>
</template>
