<script setup lang="ts">
import { computed, inject } from 'vue'
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
  Calculator,
  Activity,
  PanelLeftClose,
  PanelLeftOpen,
} from '@lucide/vue'
import { useAuth } from '../composables/useAuth'

const props = defineProps<{
  isMobile?: boolean
  isTablet?: boolean
  isSidebarOpen?: boolean
  isCollapsed?: boolean
}>()

const emit = defineEmits<{
  close: []
  'open-exchange-rate': []
  'toggle-collapse': []
}>()

const route = useRoute()
const router = useRouter()
const auth = useAuth()
const darkMode = inject<{ isDark: { value: boolean }; toggle: () => void }>('darkMode')!
const isCompact = computed(() => Boolean(!props.isMobile && (props.isTablet || props.isCollapsed)))

const navItems = [
  { section: '主選單', items: [
    { label: '儀表板', icon: LayoutDashboard, route: '/dashboard' },
    { label: '提款紀錄', icon: Banknote, route: '/withdrawals' },
    { label: '交易明細', icon: Receipt, route: '/transactions' },
    { label: '信用卡交易', icon: CreditCard, route: '/installments' },
    { label: '報表分析', icon: BarChart3, route: '/reports' },
    { label: '財務快照', icon: Camera, route: '/snapshots' },
    { label: '業務排程', icon: Activity, route: '/schedules' },
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

/** 判斷目前路由是否為導覽項目的 active 路徑。 */
const isActive = (path: string) => route.path === path

/** 點擊手機導覽項目後關閉 Drawer，桌面與平板維持原狀態。 */
function handleNavClick() {
  if (props.isMobile) {
    emit('close')
  }
}

/** 導向使用者設定，並在手機版先關閉 Drawer。 */
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
        : isCompact
          ? 'w-16'
          : 'w-60',
    ]"
  >
    <!-- Brand -->
    <div
      :class="[
        'flex transition-all duration-200',
        isCompact ? 'flex-col items-center gap-2 px-2 py-4' : 'items-center gap-3 px-3 py-6',
      ]"
    >
      <div class="w-9 h-9 shrink-0">
        <img src="/favicon.svg" alt="MyExpenses Logo" class="w-full h-full" />
      </div>
      <div v-if="!isCompact" class="flex min-w-0 flex-col">
        <span class="text-text-on-dark font-bold text-sm">MyExpenses</span>
        <span class="text-text-on-dark-muted text-xs">個人記帳</span>
      </div>
      <button
        v-if="!isMobile && !isTablet"
        type="button"
        :aria-label="isCollapsed ? '展開側邊欄' : '收合側邊欄'"
        :title="isCollapsed ? '展開側邊欄' : '收合側邊欄'"
        :class="[
          'shrink-0 rounded-lg p-1.5 text-text-on-dark-muted hover:bg-bg-sidebar-raised hover:text-text-on-dark focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-focus-ring transition-colors cursor-pointer',
          isCompact ? '' : 'ml-auto',
        ]"
        @click="emit('toggle-collapse')"
      >
        <PanelLeftOpen v-if="isCollapsed" class="w-[18px] h-[18px]" />
        <PanelLeftClose v-else class="w-[18px] h-[18px]" />
      </button>
    </div>

    <!-- Navigation -->
    <nav :class="['flex-1 overflow-y-auto', isCompact ? 'px-2' : 'px-4']">
      <template v-for="section in navItems" :key="section.section">
        <div v-if="!isCompact" class="pt-4 pb-2 px-1">
          <span class="text-text-on-dark-muted text-xs font-medium uppercase tracking-wider">{{ section.section }}</span>
        </div>
        <router-link
          v-for="item in section.items"
          :key="item.route"
          :to="item.route"
          :title="isCompact ? item.label : undefined"
          :aria-label="isCompact ? item.label : undefined"
          :class="[
            'flex items-center gap-3 rounded-lg py-2.5 text-sm transition-colors mb-1',
            isCompact ? 'justify-center px-2' : 'px-3',
            isActive(item.route)
              ? 'bg-bg-sidebar-active text-text-on-dark'
              : 'text-text-on-dark-muted hover:text-text-on-dark hover:bg-bg-sidebar-raised',
          ]"
          @click="handleNavClick"
        >
          <component :is="item.icon" class="w-[18px] h-[18px] shrink-0" :class="isActive(item.route) ? 'text-accent-primary' : ''" />
          <span v-if="!isCompact" class="whitespace-nowrap">{{ item.label }}</span>
        </router-link>
      </template>
    </nav>

    <!-- Footer: dark mode toggle + user card -->
    <div :class="['border-t border-border-sidebar-divider', isCompact ? 'p-2' : 'p-4']">
      <button
        type="button"
        :aria-label="isCompact ? (darkMode.isDark.value ? '淺色模式' : '深色模式') : undefined"
        :title="isCompact ? (darkMode.isDark.value ? '淺色模式' : '深色模式') : undefined"
        :class="[
          'flex items-center gap-3 w-full rounded-lg py-2.5 text-sm text-text-on-dark-muted hover:text-text-on-dark hover:bg-bg-sidebar-raised focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-focus-ring transition-colors cursor-pointer mb-2',
          isCompact ? 'justify-center px-2' : 'px-3',
        ]"
        @click="darkMode.toggle()"
      >
        <component :is="darkMode.isDark.value ? Sun : Moon" class="w-[18px] h-[18px] shrink-0" />
        <span v-if="!isCompact" class="whitespace-nowrap">{{ darkMode.isDark.value ? '淺色模式' : '深色模式' }}</span>
      </button>
      <button
        type="button"
        :aria-label="isCompact ? '匯率計算機' : undefined"
        :title="isCompact ? '匯率計算機' : undefined"
        :class="[
          'flex items-center gap-3 w-full rounded-lg py-2.5 text-sm text-text-on-dark-muted hover:text-text-on-dark hover:bg-bg-sidebar-raised focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-focus-ring transition-colors cursor-pointer mb-2',
          isCompact ? 'justify-center px-2' : 'px-3',
        ]"
        @click="emit('open-exchange-rate')"
      >
        <Calculator class="w-[18px] h-[18px] shrink-0" />
        <span v-if="!isCompact" class="whitespace-nowrap">匯率計算機</span>
      </button>
      <button
        type="button"
        :aria-label="isCompact ? '使用者設定' : undefined"
        :title="isCompact ? (auth.user.value?.displayName || '使用者') : undefined"
        :class="[
          'flex items-center gap-3 w-full rounded-lg py-2.5 text-sm transition-colors cursor-pointer hover:bg-bg-sidebar-raised focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-focus-ring',
          isCompact ? 'justify-center px-2' : 'px-3',
        ]"
        @click="goToSettings"
      >
        <div class="w-9 h-9 rounded-full bg-accent-primary flex items-center justify-center text-text-on-accent font-semibold text-sm shrink-0">
          {{ (auth.user.value?.displayName || 'U')[0].toUpperCase() }}
        </div>
        <div v-if="!isCompact" class="min-w-0 flex flex-col text-left">
          <span class="truncate text-text-on-dark text-sm font-medium">{{ auth.user.value?.displayName || '使用者' }}</span>
          <span class="text-text-on-dark-muted text-xs">{{ auth.user.value?.email || 'user@example.com' }}</span>
        </div>
      </button>
    </div>
  </aside>
</template>
