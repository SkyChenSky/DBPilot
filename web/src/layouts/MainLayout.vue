<script setup lang="ts">
import { computed, markRaw, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { MenuProps, TabsProps } from 'ant-design-vue'
import {
  ApiOutlined,
  BarChartOutlined,
  ClockCircleOutlined,
  DashboardOutlined,
  DatabaseOutlined,
  FileSearchOutlined,
  LineChartOutlined,
  LockOutlined,
  MenuFoldOutlined,
  MenuUnfoldOutlined,
  OrderedListOutlined,
  RadarChartOutlined,
} from '@ant-design/icons-vue'
import { useAuthStore } from '../stores/auth'
import { useTabsStore } from '../stores/tabs'
import { useInstanceContext } from '../stores/instanceContext'
import { CAP, useEngineCaps } from '../api/engine'
import { chromeTheme } from '../theme/antdTheme'
import BrandMark from '../components/BrandMark.vue'
import InstanceSelect from '../components/InstanceSelect.vue'

const router = useRouter()
const route = useRoute()
const auth = useAuthStore()
const tabs = useTabsStore()
const instanceCtx = useInstanceContext()

// ============ 侧栏折叠（148 ↔ 64，悬停浮层滑出完整菜单） ============
const COLLAPSE_KEY = 'dbpilot:sider-collapsed'

/** 折叠状态跨刷新/重登记忆（localStorage） */
const collapsed = ref(localStorage.getItem(COLLAPSE_KEY) === '1')
watch(collapsed, v => localStorage.setItem(COLLAPSE_KEY, v ? '1' : '0'))

/** 窄窗口（< 992px）自动折叠：a-layout-sider 的 breakpoint 能力在换 aside 后自补 */
const mq = window.matchMedia('(max-width: 992px)')
const onMediaChange = (e: MediaQueryListEvent) => { collapsed.value = e.matches }

/** 折叠态悬停侧栏 → 浮层滑出完整菜单（不挤压内容区，图表尺寸不受扰） */
const hovering = ref(false)

// 路由变化 → 同步 tab（key = fullPath：带参数的路由各自成 tab；无常驻首页，全部可关闭）
watch(
  () => route.fullPath,
  (path) => {
    tabs.add(path, (route.meta.title as string) || '页面')
  },
  { immediate: true },
)

async function onLogout() {
  tabs.clear()
  await auth.logout()
  router.push('/login')
}

// 侧边菜单：三组（性能优化 / 锁优化 / 请求分析），对齐 DAS；
// 下阶段实现的项在 menu 配置中标记 disabled: true 置灰展示。
const allMenuGroups = [
  {
    title: '性能优化',
    items: [
      { key: '/performance/metrics', label: '性能趋势', icon: markRaw(LineChartOutlined), disabled: false },
      { key: '/performance/insight', label: '性能洞察', icon: markRaw(RadarChartOutlined), disabled: false },
      { key: '/performance/missing-index', label: '缺失索引', icon: markRaw(FileSearchOutlined), disabled: false },
      { key: '/performance/index-usage', label: '索引使用', icon: markRaw(BarChartOutlined), disabled: false },
      { key: '/performance/topsql', label: 'Top SQL', icon: markRaw(OrderedListOutlined), disabled: false },
    ],
  },
  {
    title: '锁优化',
    items: [
      { key: '/lock/deadlocks', label: '死锁分析', icon: markRaw(ApiOutlined), disabled: false },
      { key: '/lock/blocking', label: '阻塞分析', icon: markRaw(LockOutlined), disabled: false },
    ],
  },
  {
    title: '请求分析',
    items: [{ key: '/slowlog', label: '慢日志', icon: markRaw(ClockCircleOutlined), disabled: false }],
  },
]

// 引擎过滤（能力矩阵驱动）：死锁分析在事件明细与趋势皆不可用时隐藏（MySQL/PG 有趋势-only 形态，保留入口）；
// currentEngine 为 undefined（实例未加载完/无实例）不过滤，避免首屏闪菜单
const { cap } = useEngineCaps()

const menuCapKeys: Record<string, string[]> = {
  '/lock/deadlocks': [CAP.deadlockEvents, CAP.deadlockTrend],
  '/performance/missing-index': [CAP.missingIndex],
}

const menuGroups = computed(() => {
  if (!instanceCtx.currentEngine) return allMenuGroups
  return allMenuGroups.map(g => ({
    ...g,
    // 不在能力表里的菜单永不隐藏（[].every 空数组真空真值会误删——曾经的真实事故）
    items: g.items.filter(i => !menuCapKeys[i.key]?.every(k => cap(k) === 'none')),
  }))
})

const HomeIcon = markRaw(DatabaseOutlined)
const OverviewIcon = markRaw(DashboardOutlined)

const selectedKeys = computed(() => [route.path])

const onMenuClick: MenuProps['onClick'] = (info) => {
  hovering.value = false   // 折叠浮层中导航：点完即收，不用等鼠标移开
  router.push(info.key as string)
}

onMounted(() => {
  if (mq.matches) collapsed.value = true
  mq.addEventListener('change', onMediaChange)
  // 全局实例上下文首载（顶栏选择器 + 各诊断页共用；并发调用内部去重；错误提示由 http 层统一弹出）
  instanceCtx.ensureLoaded().catch(() => {})
})

onBeforeUnmount(() => {
  mq.removeEventListener('change', onMediaChange)
})

// ============ 多标签页 ============
function onTabClick(key: string | number) {
  const target = String(key)
  if (target !== route.fullPath) router.push(target)
}

const onTabEdit: TabsProps['onEdit'] = (key, action) => {
  if (action !== 'remove') return
  const next = tabs.remove(String(key))
  if (next && next !== route.fullPath) router.push(next)
  // 无常驻 tab 后可能被关空：关掉最后一个时回实例概览兜底（路由 watch 会重新开 tab）
  else if (!next && tabs.tabs.length === 0) router.push('/overview')
}
</script>

<template>
  <!-- has-sider 必须显式声明：换成 aside 后组件检测不到 Sider 会退回纵向排列 -->
  <a-layout class="layout" has-sider>
    <!-- 不用 a-layout-sider：悬停浮层展开需要完全自控宽度（absolute 不占流），组件的 collapsed 宽度机制做不到 -->
    <aside
      class="sider"
      :class="{ collapsed, expanding: collapsed && hovering }"
      @mouseenter="hovering = true"
      @mouseleave="hovering = false"
    >
      <div class="logo">
        <BrandMark :size="24" />
        <span class="logo-name"><span class="db">DB</span>Pilot</span>
      </div>
      <a-menu
        class="sider-menu"
        theme="dark"
        mode="inline"
        :selected-keys="selectedKeys"
        @click="onMenuClick"
      >
        <!-- 图标常显（展开态带标签），折叠未悬停只显示图标居中；
             不加 title——迷你态一悬停就浮层显名，原生提示只会在浮层里冗余弹出 -->
        <a-menu-item key="/overview">
          <OverviewIcon />
          <span class="mi-label">实例概览</span>
        </a-menu-item>
        <a-menu-item key="/instance">
          <HomeIcon />
          <span class="mi-label">实例管理</span>
        </a-menu-item>
        <a-menu-item-group v-for="g in menuGroups" :key="g.title" :title="g.title">
          <a-menu-item
            v-for="item in g.items"
            :key="item.key"
            :disabled="item.disabled"
          >
            <component :is="item.icon" />
            <span class="mi-label">{{ item.label }}</span>
          </a-menu-item>
        </a-menu-item-group>
      </a-menu>
    </aside>

    <!-- 折叠悬停时 sider 脱流成浮层，此容器补 64px 占位防内容跳动 -->
    <a-layout :class="{ 'sider-ghost': collapsed && hovering }">
      <a-layout-header class="header">
        <div class="header-left">
          <button class="fold-btn" :title="collapsed ? '展开侧栏' : '折叠侧栏'" @click="collapsed = !collapsed">
            <MenuUnfoldOutlined v-if="collapsed" />
            <MenuFoldOutlined v-else />
          </button>
          <span class="title">DBPilot 数据库自治诊断平台</span>
        </div>
        <div class="header-right">
          <!-- 全局实例上下文：选一次全站生效（页面内不再重复选择；切换当前页自动重查） -->
          <a-config-provider :theme="chromeTheme">
            <InstanceSelect
              class="ctx-select"
              :value="instanceCtx.instanceId"
              :instances="instanceCtx.instances"
              :width="240"
              @change="(v: number | undefined) => v !== undefined && instanceCtx.setInstance(v)"
            />
          </a-config-provider>
          <span class="avatar">{{ (auth.username ?? '?').slice(0, 1).toUpperCase() }}</span>
          <span class="username">{{ auth.username }}</span>
          <a-button type="link" size="small" @click="onLogout">退出</a-button>
        </div>
      </a-layout-header>
      <!-- 标签条改浅色：与内容区同属浅色工作台口径，不再嵌套暗色 provider -->
      <div class="tabbar">
        <a-tabs
          :active-key="tabs.activeKey"
          type="editable-card"
          :hide-add="true"
          size="small"
          @tab-click="onTabClick"
          @edit="onTabEdit"
        >
          <a-tab-pane v-for="t in tabs.tabs" :key="t.key" :tab="t.title" :closable="t.closable" />
        </a-tabs>
      </div>
      <a-layout-content class="content">
        <router-view v-slot="{ Component }">
          <keep-alive>
            <component :is="Component" />
          </keep-alive>
        </router-view>
      </a-layout-content>
    </a-layout>
  </a-layout>
</template>

<style scoped>
.layout {
  height: 100%;
  position: relative;
}

/* 折叠悬停时右侧布局补 64px 占位（sider 脱流），内容不跳动、图表尺寸不受扰 */
.sider-ghost {
  margin-left: 64px;
}

.sider {
  width: 148px;
  flex-shrink: 0;
  transition: width 0.2s;
  background: var(--chrome-bg);
  border-right: 1px solid var(--chrome-border);
  overflow: hidden;
}

/* 收起方向一律瞬时：浮层回落流内若带宽度动画，会先以 148px 占流再缩窄，内容跳动 */
.sider.collapsed {
  width: 64px;
  transition: none;
}

/* 折叠悬停：浮层滑出完整菜单（absolute 脱流 + 阴影压住内容） */
.sider.collapsed.expanding {
  position: absolute;
  top: 0;
  bottom: 0;
  left: 0;
  z-index: 100;
  width: 148px;
  transition: width 0.2s;
  overflow: visible;
  box-shadow: 4px 0 16px rgba(0, 0, 0, 0.35);
}

/* 折叠态（未悬停）：品牌块居中、分组标题隐藏、菜单项缩成圆点 */
.sider.collapsed .logo {
  justify-content: center;
  padding: 0;
  gap: 0;
}

.sider.collapsed .logo-name {
  display: none;
}

.sider.collapsed:not(.expanding) :deep(.ant-menu-item-group-title) {
  display: none;
}

.sider.collapsed:not(.expanding) :deep(.ant-menu-item) {
  padding-inline: 0 !important;
}

/* title-content 是 flex:auto 占满行宽且内容左对齐，图标居中必须在它内部做 */
.sider.collapsed:not(.expanding) :deep(.ant-menu-item .ant-menu-title-content) {
  display: flex;
  justify-content: center;
}

/* 菜单项图标仅折叠迷你态显示（展开与浮层态纯文字，与字标风格一致） */
.sider :deep(.ant-menu-item .anticon) {
  display: none;
}

.sider.collapsed:not(.expanding) :deep(.ant-menu-item .anticon) {
  display: inline-flex;
  margin-inline-end: 0;
  font-size: 16px;
}

.sider.collapsed:not(.expanding) .mi-label {
  display: none;
}

/* 侧栏纵向：logo 固定 + 菜单区独立滚动（矮视口不挤掉底部菜单项） */
.sider {
  display: flex;
  flex-direction: column;
}

.sider-menu {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  overflow-x: hidden;
  border-inline-end: none !important;
}

/* 暗壳滚动条 */
.sider-menu::-webkit-scrollbar {
  width: 6px;
}

.sider-menu::-webkit-scrollbar-thumb {
  background: var(--chrome-border);
  border-radius: 3px;
}

.sider-menu::-webkit-scrollbar-track {
  background: transparent;
}

.logo {
  height: 56px;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 0 16px;
  border-bottom: 1px solid var(--chrome-border-split);
}

.logo-name {
  font-size: 17px;
  font-weight: 700;
  letter-spacing: 1px;
  color: var(--chrome-text-primary);
}

.logo-name .db {
  color: var(--chrome-primary);
}

/* 菜单选中态左侧 2px 主色强调条（淡底选中来自 Menu token） */
.sider-menu :deep(.ant-menu-item-selected) {
  position: relative;
}

.sider-menu :deep(.ant-menu-item-selected)::before {
  content: '';
  position: absolute;
  left: 0;
  top: 25%;
  bottom: 25%;
  width: 2px;
  border-radius: 1px;
  background: var(--chrome-primary);
}

.header {
  background: var(--chrome-bg);
  border-bottom: 1px solid var(--chrome-border);
  padding: 0 16px 0 12px;
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.header-left {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

/* 折叠按钮（暗壳上）：纯 CSS 箭头，无图标库 */
.fold-btn {
  width: 28px;
  height: 28px;
  border: none;
  border-radius: 4px;
  background: transparent;
  color: var(--chrome-text-secondary);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
}

.fold-btn:hover {
  color: var(--chrome-text-primary);
  background: var(--chrome-panel);
}

/* 图标字号（antd 图标默认继承字号，16px 与暗壳标题协调） */
.fold-btn :deep(.anticon) {
  font-size: 16px;
}

.title {
  font-size: 13px;
  font-weight: 500;
  color: var(--chrome-text-secondary);
}

.header-right {
  display: flex;
  align-items: center;
  gap: 8px;
}

/* 顶栏全局实例选择器（暗壳）：小号 + 适配窄窗口收缩 */
.ctx-select {
  flex-shrink: 1;
  min-width: 120px;
}

.avatar {
  width: 24px;
  height: 24px;
  border-radius: 50%;
  background: var(--chrome-panel);
  border: 1px solid var(--chrome-border);
  color: var(--chrome-primary);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  font-size: 12px;
  font-weight: 600;
}

.username {
  color: var(--chrome-text-secondary);
}

/* 多标签条（浅色工作台口径）：白底条 + 透明 tab，
   active = 主色淡底 + 顶部 2px 主色强调线 + 主色文字（浏览器标签式） */
.tabbar {
  background: var(--panel-bg);
  border-bottom: 1px solid var(--border-color-split);
  padding: 6px 8px 0;
}

.tabbar :deep(.ant-tabs-nav) {
  margin: 0;
}

/* 去组件自带底线，统一用 tabbar 的 border-bottom */
.tabbar :deep(.ant-tabs-nav::before) {
  display: none;
}

.tabbar :deep(.ant-tabs-tab) {
  background: transparent !important;
  border: 1px solid transparent !important;
  border-bottom: none !important;
  border-radius: 4px 4px 0 0 !important;
}

.tabbar :deep(.ant-tabs-tab .ant-tabs-tab-btn) {
  color: var(--text-secondary);
}

.tabbar :deep(.ant-tabs-tab:hover .ant-tabs-tab-btn) {
  color: var(--text-primary);
}

.tabbar :deep(.ant-tabs-tab.ant-tabs-tab-active) {
  background: var(--color-primary-bg) !important;
  border-color: transparent !important;
  box-shadow: inset 0 2px 0 var(--color-primary);
}

.tabbar :deep(.ant-tabs-tab-active .ant-tabs-tab-btn) {
  color: var(--color-primary) !important;
}

.tabbar :deep(.ant-tabs-tab-remove) {
  color: var(--text-tertiary);
}

.tabbar :deep(.ant-tabs-tab-remove:hover) {
  color: var(--danger-text) !important;
}

/* 滚动上移到内容层：去双重 padding（页面根 div 自带 16px），页面迁移后根挂 .page */
.content {
  padding: 0;
  background: var(--bg-body);
  min-height: 0;
  overflow: auto;
}
</style>
