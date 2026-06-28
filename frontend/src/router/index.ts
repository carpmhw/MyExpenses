import { createRouter, createWebHistory } from 'vue-router'
import { useAuth } from '../composables/useAuth'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', redirect: '/dashboard' },
    {
      path: '/login',
      name: 'login',
      component: () => import('../pages/login/index.vue'),
      meta: { title: '登入', public: true },
    },
    {
      path: '/dashboard',
      name: 'dashboard',
      component: () => import('../pages/dashboard/index.vue'),
      meta: { title: '儀表板' },
    },
    {
      path: '/transactions',
      name: 'transactions',
      component: () => import('../pages/expenses/index.vue'),
      meta: { title: '交易明細' },
    },
    {
      path: '/expenses',
      redirect: '/transactions',
    },
    {
      path: '/withdrawals',
      name: 'withdrawals',
      component: () => import('../pages/withdrawals/index.vue'),
      meta: { title: '提款紀錄' },
    },
    {
      path: '/installments',
      name: 'installments',
      component: () => import('../pages/installments/index.vue'),
      meta: { title: '信用卡分期' },
    },
    {
      path: '/reports',
      name: 'reports',
      component: () => import('../pages/reports/index.vue'),
      meta: { title: '報表分析' },
    },
    {
      path: '/categories',
      name: 'categories',
      component: () => import('../pages/categories/index.vue'),
      meta: { title: '分類管理' },
    },
    {
      path: '/payment-methods',
      name: 'payment-methods',
      component: () => import('../pages/payment-methods/index.vue'),
      meta: { title: '支付方式管理' },
    },
    {
      path: '/stocks',
      name: 'stocks',
      component: () => import('../pages/stocks/index.vue'),
      meta: { title: '股票管理' },
    },
    {
      path: '/bank-accounts',
      name: 'bank-accounts',
      component: () => import('../pages/bank-accounts/index.vue'),
      meta: { title: '銀行帳戶管理' },
    },
    {
      path: '/credit-cards',
      name: 'credit-cards',
      component: () => import('../pages/credit-cards/index.vue'),
      meta: { title: '信用卡管理' },
    },
    {
      path: '/snapshots',
      name: 'snapshots',
      component: () => import('../pages/snapshots/index.vue'),
      meta: { title: '財務快照' },
    },
    {
      path: '/snapshots/compare',
      name: 'snapshot-compare',
      component: () => import('../pages/snapshots/compare.vue'),
      meta: { title: '快照比對' },
    },
    {
      path: '/settings',
      name: 'settings',
      component: () => import('../pages/settings/index.vue'),
      meta: { title: '使用者設定' },
    },
    {
      path: '/:pathMatch(.*)*',
      name: 'not-found',
      component: () => import('../pages/NotFound.vue'),
      meta: { title: '404' },
    },
  ],
})

router.beforeEach((to, _from, next) => {
  const { isAuthenticated } = useAuth()
  if (to.meta.public) {
    next()
  } else if (!isAuthenticated.value) {
    next('/login')
  } else {
    next()
  }
})

export default router
