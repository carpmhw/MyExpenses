<script setup lang="ts">
import { ref } from 'vue'
import Sidebar from './Sidebar.vue'
import MobileHeader from '../components/ui/MobileHeader.vue'
import ExchangeRateDialog from '../components/exchange-rate/ExchangeRateDialog.vue'
import { useSidebar } from '../composables/useSidebar'

const { isMobile, isTablet, isSidebarOpen, openSidebar, closeSidebar } = useSidebar()
const exchangeRateOpen = ref(false)
</script>

<template>
  <div class="flex h-screen overflow-hidden">
    <MobileHeader v-if="isMobile" :on-menu-click="openSidebar" />
    <div v-if="isMobile" class="flex-1 flex flex-col overflow-hidden">
      <div class="flex-1 overflow-y-auto bg-bg-app">
        <router-view />
      </div>
    </div>
    <template v-else>
      <Sidebar :is-tablet="isTablet" @open-exchange-rate="exchangeRateOpen = true" />
      <main class="flex-1 overflow-y-auto bg-bg-app">
        <router-view />
      </main>
    </template>
    <Sidebar
      v-if="isMobile"
      :is-mobile="true"
      :is-sidebar-open="isSidebarOpen"
      @close="closeSidebar"
      @open-exchange-rate="exchangeRateOpen = true"
    />
    <ExchangeRateDialog :open="exchangeRateOpen" @update:open="exchangeRateOpen = $event" />
  </div>
</template>
