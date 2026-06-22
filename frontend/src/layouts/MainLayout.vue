<script setup lang="ts">
import Sidebar from './Sidebar.vue'
import MobileHeader from '../components/ui/MobileHeader.vue'
import { useSidebar } from '../composables/useSidebar'

const { isMobile, isTablet, isSidebarOpen, openSidebar, closeSidebar } = useSidebar()
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
      <Sidebar :is-tablet="isTablet" />
      <main class="flex-1 overflow-y-auto bg-bg-app">
        <router-view />
      </main>
    </template>
    <Sidebar
      v-if="isMobile"
      :is-mobile="true"
      :is-sidebar-open="isSidebarOpen"
      @close="closeSidebar"
    />
  </div>
</template>
