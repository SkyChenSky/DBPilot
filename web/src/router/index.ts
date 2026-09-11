import { createRouter, createWebHistory } from 'vue-router'
import MainLayout from '../layouts/MainLayout.vue'
import { useAuthStore } from '../stores/auth'

/**
 * 页面路由树（路由 path 与菜单/侧栏一致）。
 * /login 为公开页；其余页面需登录（路由守卫探测会话）。
 */
const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/login',
      name: 'login',
      component: () => import('../views/Login.vue'),
      meta: { public: true },
    },
    {
      path: '/',
      component: MainLayout,
      children: [
        { path: '', redirect: '/overview' },
        { path: 'overview', name: 'instance-overview', component: () => import('../views/instance/Overview.vue'), meta: { title: '实例概览' } },
        { path: 'instance', name: 'instance-list', component: () => import('../views/instance/List.vue'), meta: { title: '实例管理' } },
        { path: 'performance/insight', name: 'performance-insight', component: () => import('../views/performance/PerformanceInsight.vue'), meta: { title: '性能洞察' } },
        { path: 'performance/missing-index', name: 'missing-index', component: () => import('../views/performance/MissingIndex.vue'), meta: { title: '缺失索引' } },
        { path: 'performance/index-usage', name: 'index-usage', component: () => import('../views/performance/IndexUsage.vue'), meta: { title: '索引使用' } },
        { path: 'performance/topsql', name: 'top-sql', component: () => import('../views/performance/TopSql.vue'), meta: { title: 'Top SQL' } },
        { path: 'performance/metrics', name: 'metrics-trend', component: () => import('../views/performance/MetricsTrend.vue'), meta: { title: '性能趋势' } },
        { path: 'lock/deadlocks', name: 'deadlock-list', component: () => import('../views/lock/DeadlockList.vue'), meta: { title: '死锁分析' } },
        { path: 'lock/deadlocks/:eventId', name: 'deadlock-detail', component: () => import('../views/lock/DeadlockDetail.vue'), meta: { title: '死锁详情' } },
        { path: 'lock/blocking', name: 'blocking', component: () => import('../views/lock/Blocking.vue'), meta: { title: '阻塞分析' } },
        { path: 'slowlog', name: 'slow-sql', component: () => import('../views/slowlog/SlowSql.vue'), meta: { title: '慢日志' } },
      ],
    },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
})

router.beforeEach(async (to) => {
  const auth = useAuthStore()
  await auth.ensureChecked()

  if (to.meta.public) {
    return auth.isLoggedIn ? { path: '/' } : true
  }
  if (!auth.isLoggedIn) {
    return { path: '/login' }
  }
  return true
})

export default router
