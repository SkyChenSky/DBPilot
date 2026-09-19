<script setup lang="ts">
import { computed, nextTick, onActivated, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import dayjs from 'dayjs'
import type { ECharts } from 'echarts/core'
import {
  getBlockingCurrent,
  getBlockingEvents,
  getBlockingEventDetail,
  getBlockingStats,
  getBlockingTrend,
  type BlockingEventDetail,
  type BlockingEventItem,
  type BlockingNode,
  type BlockingOverview,
  type BlockingStats,
  type BlockingTrend,
} from '../../api/blocking'
import { listDatabases } from '../../api/instance'
import BlockTreeNode from '../../components/BlockTreeNode.vue'
import EllipsisText from '../../components/EllipsisText.vue'
import SqlText from '../../components/SqlText.vue'
import StatCard from '../../components/StatCard.vue'
import { CHART_COLORS, initChart } from '../../charts/echarts'
import { palette } from '../../theme/palette'
import { useGlobalInstance } from '../../composables/useGlobalInstance'
import { usePolling } from '../../composables/usePolling'
import { useTimeRange } from '../../composables/useTimeRange'
import { fitColumnWidth, TIME_COL_W } from '../../utils/fitColumnWidth'
import { fmtDur, fmtMsUnit, fmtTime } from '../../utils/format'

// 实例级诊断（无库维度）：实例来自顶栏全局上下文，首载/切换 → onInstanceChange 立即拉一次
const { instanceId, init } = useGlobalInstance(() => onInstanceChange())
const overview = ref<BlockingOverview>()
const autoRefresh = ref(true)

/** 实例级诊断（无库维度）：换实例立即拉一次 */
const { start: startPolling, stop: stopPolling } = usePolling(fetch, 5_000)

async function fetch() {
  if (instanceId.value === undefined) return
  overview.value = await getBlockingCurrent(instanceId.value)
}

async function onInstanceChange() {
  loadDbOptions()
  if (tab.value === 'history') await searchEvents()
  else await fetch()
}

function toggleAuto(checked: boolean) {
  if (checked) startPolling()
  else stopPolling()
}

// ---------- 历史 / 实时 tab：默认历史（对齐阿里云），实时 tab 才开轮询 ----------
const tab = ref('history')

watch(tab, (t) => {
  if (t === 'history') {
    stopPolling()
    searchEvents()
  } else if (autoRefresh.value) {
    startPolling()
  }
})

// ---------- 历史事件列表 ----------
const histLoading = ref(false)
const histRows = ref<BlockingEventItem[]>([])
const histTotal = ref(0)
const histQuery = reactive({ page: 1, limit: 10 })
/** all = 全部 / ongoing = 进行中 / resolved = 已解除（哨兵字符串，select 的 undefined value 选不回） */
const histStatus = ref<'all' | 'ongoing' | 'resolved'>('all')

/** 库筛选（按留痕头库过滤；'' = 全部库。选项来自实例在线库列表） */
const histDb = ref('')
const dbOptions = ref<{ value: string; label: string }[]>([{ value: '', label: '全部库' }])

async function loadDbOptions() {
  histDb.value = ''
  if (instanceId.value === undefined) return
  try {
    const dbs = await listDatabases(instanceId.value)
    dbOptions.value = [{ value: '', label: '全部库' }, ...dbs.map(d => ({ value: d, label: d }))]
  } catch {
    // 库列表拉取失败不阻断主流程（保留"全部库"）
  }
}

/** 时间档位：默认 24h（档位↔自定义联动统一走 useTimeRange） */
const {
  rangeKey: histRangeKey, rangeOptions: histRangeOptions, customRange: histCustomRange,
  onPresetChange: onHistPresetChange, onCustomChange: onHistCustomChange, fromTo: histFromTo,
  disabledDate: histDisabledDate,
} = useTimeRange({ defaultKey: '24h', onApply: () => searchEvents() })

async function loadEvents() {
  if (instanceId.value === undefined) return
  histLoading.value = true
  try {
    const { from, to } = histFromTo()
    const data = await getBlockingEvents(instanceId.value, {
      page: histQuery.page,
      limit: histQuery.limit,
      resolved: histStatus.value === 'all' ? undefined : histStatus.value === 'resolved',
      dbName: histDb.value || undefined,
      from,
      to,
    })
    histRows.value = data.items
    histTotal.value = data.total
  } finally {
    histLoading.value = false
  }
}

function searchEvents() {
  histQuery.page = 1
  loadEvents()
  loadStats()
  loadTrend()
}

function onEventPageChange(p: { current: number; pageSize: number }) {
  histQuery.page = p.current
  histQuery.limit = p.pageSize
  loadEvents()
}

/** 事件持续秒数：已解除 = end - start；进行中 = 至今 */
function eventDuration(e: BlockingEventItem) {
  const start = dayjs(e.startTimeUtc)
  const end = e.endTimeUtc ? dayjs(e.endTimeUtc) : dayjs()
  return Math.max(0, end.diff(start, 'second'))
}


// ---------- 统计卡 + 阻塞趋势（对齐阿里云锁阻塞页：近 1 天/1 周/2 周数量 + 双 Y 轴趋势图） ----------
const histStats = ref<BlockingStats>()
const histTrend = ref<BlockingTrend>()

async function loadStats() {
  if (instanceId.value === undefined) return
  histStats.value = await getBlockingStats(instanceId.value)
}

const trendEl = ref<HTMLDivElement>()
let trendChart: ECharts | null = null

async function loadTrend() {
  if (instanceId.value === undefined) return
  const { from, to } = histFromTo()
  if (!from || !to) {
    histTrend.value = undefined
    return
  }
  histTrend.value = await getBlockingTrend(instanceId.value, { from, to })
  await nextTick()
  renderTrend()
}

/** 双 Y 轴：柱 = 等待秒累计（阻塞严重度）/ 线 = 事件次数；跟随时间筛选，桶粒度后端自适应。
 *  轴不写 name（顶部与图例撞），轴数字走主题统一色。 */
function renderTrend() {
  const el = trendEl.value
  if (!el) return
  if (!trendChart || trendChart.isDisposed()) trendChart = initChart(el)
  const items = histTrend.value?.items ?? []
  trendChart.setOption({
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: 'shadow' },
      valueFormatter: (v: number) => (v == null ? '-' : v.toLocaleString()),
    },
    legend: {
      data: ['等待秒累计', '次数'],
      icon: 'roundRect',
      itemWidth: 14,
      itemHeight: 4,
      top: 0,
    },
    grid: { left: 8, right: 8, top: 32, bottom: 4, containLabel: true },
    xAxis: {
      type: 'category',
      data: items.map(i => dayjs(i.bucketStartUtc).format('MM-DD HH:mm')),
      axisLabel: { hideOverlap: true },
    },
    yAxis: [
      { type: 'value', alignTicks: true },
      {
        type: 'value',
        minInterval: 1,
        alignTicks: true,
        splitLine: { show: false },   // 只留左轴网格线，双轴横线错位很脏
      },
    ],
    series: [
      {
        name: '等待秒累计',
        type: 'bar',
        data: items.map(i => i.totalWaitSeconds),
        barMaxWidth: 18,
        itemStyle: { color: CHART_COLORS.warning, borderRadius: [3, 3, 0, 0] },
      },
      {
        name: '次数',
        type: 'line',
        yAxisIndex: 1,
        data: items.map(i => i.count),
        smooth: true,
        lineStyle: { width: 2 },
        symbol: 'circle',
        symbolSize: 6,
        itemStyle: { color: CHART_COLORS.purple, borderColor: '#fff', borderWidth: 1.5 },
      },
    ],
    graphic: items.length ? [] : [{
      type: 'text',
      left: 'center',
      top: 'middle',
      style: { text: '暂无数据', fill: palette.text.tertiary, fontSize: 13 },
    }],
  }, true)
}

// ---------- 事件详情弹窗（链路快照复用 BlockTreeNode 缩进列表） ----------
const detailOpen = ref(false)
const detailLoading = ref(false)
const detail = ref<BlockingEventDetail>()

// 头会话列量测拼上「会话ID + 全角空格」前缀
// （单元格实际渲染两者拼接，只量 headInfo 会让前缀挤掉文本宽度 → 未超长也出省略号）
const headW = computed(() => fitColumnWidth(histRows.value.map(r => `${r.headSessionId ?? ''}　${r.headInfo ?? ''}`), { min: 170, max: 340 }))
const dbW = computed(() => fitColumnWidth(histRows.value.map(r => r.headDbName ?? '-'), { min: 90, max: 160 }))

const eventColumns = computed(() => [
  { title: '#', key: 'seq', width: 48 },
  { title: '开始时间', key: 'startTime', width: TIME_COL_W },
  { title: '持续时长', key: 'duration', width: 100 },
  { title: '头会话', key: 'head', width: headW.value },
  { title: '根阻塞 SQL', key: 'headSql', width: 260 },
  { title: '数据库', key: 'db', width: dbW.value },
  { title: '被阻塞', dataIndex: 'blockedCount', width: 70 },
  { title: '最长等待', key: 'maxWait', width: 85 },
  { title: '状态', key: 'resolved', width: 75 },
  { title: '操作', key: 'op', width: 60, fixed: 'right' as const },
])

async function openDetail(id: number) {
  detailLoading.value = true
  detail.value = undefined
  detailOpen.value = true
  try {
    detail.value = await getBlockingEventDetail(id)
  } finally {
    detailLoading.value = false
  }
}

// SQL 全文弹窗
const sqlOpen = ref(false)
const sqlFull = ref('')
const batchFull = ref('')
function showSql(n: BlockingNode) {
  sqlFull.value = n.sqlText ?? ''
  batchFull.value = n.batchSqlText ?? ''
  sqlOpen.value = true
}

// ---------- 阻塞关系图（按链查看：点"查看关系图"展开当前链，ECharts 力导向图） ----------
const expandedId = ref<number>()
const chartEls = new Map<number, HTMLDivElement>()
const charts = new Map<number, ECharts>()

function setChartEl(id: number, el: any) {
  if (el) chartEls.set(id, el as HTMLDivElement)
  else chartEls.delete(id)
}

async function toggleChain(id: number) {
  if (expandedId.value === id) {
    closeChain()
    return
  }
  closeChain()   // 同时只展开一条链
  expandedId.value = id
  await nextTick()
  const tree = overview.value?.trees.find(t => t.sessionId === id)
  if (tree) renderTreeChart(tree)
}

function closeChain() {
  if (expandedId.value === undefined) return
  charts.get(expandedId.value)?.dispose()
  charts.delete(expandedId.value)
  expandedId.value = undefined
}

/** 节点角色样式：浅色填充 + 彩描边（与死锁关系图同风格），色值取 palette 单一来源 */
function nodeStyle(n: BlockingNode, root: boolean) {
  if (n.isSystem) return { fill: palette.bg.code, border: palette.text.tertiary, text: '系统', bg: palette.text.tertiary }
  if (n.isSleepingHead) return { fill: palette.warning.bg, border: palette.warning.text, text: '睡着拿锁', bg: palette.warning.text }
  if (root) return { fill: palette.danger.bg, border: palette.danger.text, text: '根阻塞', bg: palette.danger.text }
  return { fill: palette.brand.primaryBg, border: palette.brand.primary, text: '被阻塞', bg: palette.brand.primary }
}

function renderTreeChart(tree: BlockingNode) {
  const el = chartEls.get(tree.sessionId)
  if (!el) return
  let chart = charts.get(tree.sessionId)
  if (!chart || chart.isDisposed()) {
    chart = initChart(el)
    charts.set(tree.sessionId, chart)
  }

  // 树形分层布局（确定性，替代力导向）：叶子按序均匀铺开，内点取子节点 y 均值，x 按深度
  const gapY = 78
  const stepX = 190
  let leafIdx = 0
  const nodes: any[] = []
  const links: any[] = []

  function walk(n: BlockingNode, depth: number): number {
    const childYs = n.children.map(c => walk(c, depth + 1))
    const y = childYs.length
      ? (Math.min(...childYs) + Math.max(...childYs)) / 2
      : leafIdx++ * gapY

    const s = nodeStyle(n, depth === 0)
    nodes.push({
      id: String(n.sessionId),
      x: 30 + depth * stepX,
      y,
      name: String(n.sessionId),
      symbolSize: depth === 0 ? 44 : 36,
      itemStyle: { color: s.fill, borderColor: s.border, borderWidth: depth === 0 ? 3 : 2 },
      raw: n,
      root: depth === 0,
      label: {
        show: true,
        position: 'top',
        distance: 8,
        formatter: `{id|会话 ${n.sessionId}} {tag|${s.text}}\n{sub|${`${n.loginName ?? '-'}@${n.hostName ?? '-'}`.slice(0, 20)}}\n{sub|${[n.programName ?? '', n.dbName ?? ''].filter(Boolean).join(' · ').slice(0, 22)}}`,
        rich: {
          id: { fontSize: 13, fontWeight: 'bold', color: s.border, lineHeight: 18 },
          tag: { fontSize: 10, color: '#fff', backgroundColor: s.bg, padding: [1, 4], borderRadius: 3 },
          sub: { fontSize: 10, color: palette.text.tertiary, lineHeight: 13 },
        },
      },
    })
    for (const c of n.children)
      links.push({
        source: String(n.sessionId),
        target: String(c.sessionId),
        symbol: ['none', 'arrow'],
        symbolSize: 9,
        lineStyle: { color: palette.warning.text, width: 2 },
        label: {
          show: true,
          formatter: `${c.waitType ?? ''} ${fmtMsUnit(c.waitTimeMs)}`,
          fontSize: 10,
          color: palette.warning.text,
          backgroundColor: palette.warning.bg,
          padding: [1, 4],
          borderRadius: 3,
        },
      })
    return y
  }
  walk(tree, 0)

  // 叶子多时加高画布（布局 y 是绝对坐标，容器必须够高）
  el.style.height = `${Math.max(300, leafIdx * gapY + 70)}px`
  chart.resize()

  chart.setOption({
    tooltip: {
      confine: true,
      formatter: (p: any) => {
        if (!p.dataType || p.dataType !== 'node') return ''
        const n: BlockingNode = p.data.raw
        const lines = [
          `<b>会话 ${n.sessionId}</b>${n.isSystem ? '（系统节点）' : ''}${n.isSleepingHead ? '（睡着拿锁）' : ''}`,
          n.loginName ? `${n.loginName}@${n.hostName ?? '-'}${n.programName ? `（${n.programName}）` : ''}` : '',
          n.dbName ? `库：${n.dbName}` : '',
          n.waitType ? `等待：${n.waitType} ${fmtMsUnit(n.waitTimeMs)}` : '',
          n.totalElapsedMs != null ? `已执行：${fmtMsUnit(n.totalElapsedMs)}` : '',
          n.openTranCount > 0 ? `开事务：${n.openTranCount}` : '',
          n.sqlText ? `SQL：${n.sqlText.slice(0, 80).replace(/\s+/g, ' ')}` : '',
          ...(n.locks ?? []).slice(0, 3).map(l =>
              `${l.lockStatus === 'WAIT' ? '等待锁' : '持有锁'}：${l.lockMode} ${l.objectName ?? l.dbName ?? ''}`),
        ].filter(Boolean)
        return lines.join('<br/>')
      },
    },
    series: [{
      type: 'graph',
      layout: 'none',
      roam: true,
      data: nodes,
      links,
      emphasis: { focus: 'adjacency', lineStyle: { width: 3 } },
    }],
  }, true)
}

// 轮询更新：只重绘当前展开的链；链消失（阻塞解除）自动收起
watch(() => overview.value, (o) => {
  if (expandedId.value === undefined) return
  const tree = o?.trees.find(t => t.sessionId === expandedId.value)
  if (!tree) {
    closeChain()
    return
  }
  nextTick(() => renderTreeChart(tree))
})

function onResize() {
  charts.forEach(c => !c.isDisposed() && c.resize())
  trendChart && !trendChart.isDisposed() && trendChart.resize()
}

// keep-alive 切回：容器重新挂载，按当前尺寸重排（缓存期间窗口/侧栏变化的兜底）
onActivated(() => onResize())

onMounted(() => {
  init()
  // 默认历史 tab 不轮询；实时 tab 由 watch(tab) 开启（immediate 立即执行一次且内部吞错）
  if (autoRefresh.value && tab.value !== 'history') startPolling()
  window.addEventListener('resize', onResize)
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', onResize)
  charts.forEach(c => c.dispose())
  charts.clear()
  trendChart?.dispose()
  trendChart = null
})
</script>

<template>
  <div class="page">
    <a-card>
      <!-- 工具栏（实例在顶栏全局选择，两 tab 共用） -->
      <div v-if="tab === 'realtime'" class="toolbar" style="margin-bottom: 16px">
        <a-switch v-model:checked="autoRefresh" checked-children="自动刷新" un-checked-children="已暂停" @change="toggleAuto" />
        <span v-if="overview" class="text-tertiary">更新于 {{ fmtTime(overview.snapshotTimeUtc, 'HH:mm:ss') }}</span>
      </div>

      <a-tabs v-model:active-key="tab">
        <!-- 历史阻塞（dbpilot_blocking_event 留痕） -->
        <a-tab-pane key="history" tab="历史阻塞">
          <!-- 统计卡：近一天 / 近一周 / 近两周阻塞数量（对齐阿里云） -->
          <div class="stat-grid">
            <StatCard title="近一天的阻塞数量" :value="histStats?.dayCount ?? 0" unit="条" />
            <StatCard title="近一周的阻塞数量" :value="histStats?.weekCount ?? 0" unit="条" />
            <StatCard title="近两周的阻塞数量" :value="histStats?.twoWeekCount ?? 0" unit="条" />
          </div>

          <div class="toolbar" style="margin-bottom: 12px">
            <span>状态：</span>
            <a-select
              v-model:value="histStatus"
              style="width: 120px"
              :options="[
                { value: 'all', label: '全部' },
                { value: 'ongoing', label: '进行中' },
                { value: 'resolved', label: '已解除' },
              ]"
              @change="searchEvents"
            />
            <span>库：</span>
            <a-select v-model:value="histDb" style="width: 180px" :options="dbOptions" @change="searchEvents" />
            <span>时间：</span>
            <a-select :value="histRangeKey" style="width: 140px" :options="histRangeOptions" @change="onHistPresetChange" />
            <a-range-picker
              v-model:value="histCustomRange"
              show-time
              format="YYYY-MM-DD HH:mm"
              :placeholder="['开始时间', '结束时间']"
              :disabled-date="histDisabledDate"
              @change="onHistCustomChange"
            />
            <a-button type="primary" @click="searchEvents">刷新</a-button>
          </div>

          <!-- 阻塞趋势：柱 = 等待秒累计 / 线 = 事件次数（跟随时间筛选） -->
          <div ref="trendEl" style="height: 260px; margin-bottom: 12px"></div>

          <a-table
            :columns="eventColumns"
            :data-source="histRows"
            :loading="histLoading"
            row-key="id"
            size="small"
            :scroll="{ x: 698 + TIME_COL_W + headW + dbW }"
            :pagination="{
              current: histQuery.page,
              pageSize: histQuery.limit,
              total: histTotal,
              showSizeChanger: true,
              showTotal: (t: number) => `共 ${t} 条`,
            }"
            @change="onEventPageChange"
          >
            <template #bodyCell="{ column, record, index }">
              <template v-if="column.key === 'seq'">
                {{ (histQuery.page - 1) * histQuery.limit + index + 1 }}
              </template>
              <template v-else-if="column.key === 'startTime'">
                {{ fmtTime(record.startTimeUtc) }}
              </template>
              <template v-else-if="column.key === 'duration'">
                <template v-if="record.resolved">{{ fmtDur(eventDuration(record)) }}</template>
                <span v-else style="color: var(--danger-text)">至今 {{ fmtDur(eventDuration(record)) }}</span>
              </template>
              <template v-else-if="column.key === 'head'">
                <span style="font-weight: 600">{{ record.headSessionId }}</span>
                <span style="color: var(--text-secondary)">　<EllipsisText :text="record.headInfo" viewer-title="头会话" /></span>
              </template>
              <template v-else-if="column.key === 'headSql'">
                <EllipsisText :text="record.headSql" viewer-title="根阻塞 SQL" mono />
              </template>
              <template v-else-if="column.key === 'db'">
                {{ record.headDbName ?? '-' }}
              </template>
              <template v-else-if="column.key === 'maxWait'">
                {{ fmtDur(record.maxWaitSeconds) }}
              </template>
              <template v-else-if="column.key === 'resolved'">
                <a-tag v-if="record.resolved">已解除</a-tag>
                <a-tag v-else color="red">进行中</a-tag>
              </template>
              <template v-else-if="column.key === 'op'">
                <a-button size="small" type="link" @click="openDetail(record.id)">详情</a-button>
              </template>
            </template>
          </a-table>
        </a-tab-pane>

        <!-- 实时阻塞 -->
        <a-tab-pane key="realtime" tab="实时阻塞">
          <!-- 总览卡片 -->
          <template v-if="overview">
            <div v-if="overview.chainCount > 0" class="stat-grid">
              <StatCard title="阻塞链" :value="overview.chainCount" unit="条" tone="danger" />
              <StatCard title="最长阻塞" :value="fmtDur(overview.maxWaitSeconds)" />
              <StatCard title="涉及会话" :value="overview.involvedSessions" unit="个" />
            </div>

            <a-empty
              v-if="overview.chainCount === 0"
              description="当前无阻塞"
              :image="undefined"
              style="padding: 48px 0"
            />

            <!-- 阻塞树森林：每棵 = 一个根阻塞者；点"查看关系图"按链展开 -->
            <div v-else class="forest">
              <div v-for="t in overview.trees" :key="t.sessionId" class="tree">
                <div class="tree-head">
                  <a-button size="small" type="link" @click="toggleChain(t.sessionId)">
                    {{ expandedId === t.sessionId ? '收起关系图' : '查看关系图' }}
                  </a-button>
                </div>
                <BlockTreeNode :node="t" root @show-sql="showSql" />
                <div
                  v-if="expandedId === t.sessionId"
                  :ref="(el: any) => setChartEl(t.sessionId, el)"
                  class="chain-chart"
                ></div>
                <div v-if="expandedId === t.sessionId" class="text-tertiary" style="margin-top: 6px; font-size: 12px">
                  左 = 阻塞源头，向右逐级展开｜红 = 根阻塞｜橙 = 睡着拿锁｜蓝 = 被阻塞｜灰 = 系统会话｜
                  橙箭头 = 阻塞方向（标签为等待类型和时长）｜悬停看详情，滚轮缩放可拖拽
                </div>
              </div>
            </div>
          </template>
          <a-empty v-else-if="instanceId !== undefined" description="加载中…" style="padding: 48px 0" />
        </a-tab-pane>
      </a-tabs>
    </a-card>

    <!-- 历史事件详情：元信息 + 链路快照（最近一次采样的阻塞树） -->
    <a-modal v-model:open="detailOpen" :title="`阻塞事件 #${detail?.id ?? ''}`" :width="860" :footer="null">
      <a-empty v-if="detailLoading" description="加载中…" style="padding: 48px 0" />
      <template v-else-if="detail">
        <a-descriptions :column="3" size="small" bordered style="margin-bottom: 12px">
          <a-descriptions-item label="开始时间">{{ fmtTime(detail.startTimeUtc) }}</a-descriptions-item>
          <a-descriptions-item label="结束时间">{{ detail.endTimeUtc ? fmtTime(detail.endTimeUtc) : '—' }}</a-descriptions-item>
          <a-descriptions-item label="持续时长">{{ fmtDur(eventDuration(detail)) }}</a-descriptions-item>
          <a-descriptions-item label="头会话" :span="2">{{ detail.headSessionId }}（{{ detail.headInfo ?? '-' }}）</a-descriptions-item>
          <a-descriptions-item label="状态">
            <a-tag v-if="detail.resolved">已解除</a-tag>
            <a-tag v-else color="red">进行中</a-tag>
          </a-descriptions-item>
          <a-descriptions-item label="被阻塞会话">{{ detail.blockedCount }}</a-descriptions-item>
          <a-descriptions-item label="最长等待">{{ fmtDur(detail.maxWaitSeconds) }}</a-descriptions-item>
          <a-descriptions-item label="链路快照">最近一次采样</a-descriptions-item>
        </a-descriptions>
        <BlockTreeNode v-if="detail.tree" :node="detail.tree" root @show-sql="showSql" />
        <a-empty v-else description="无链路快照" style="padding: 32px 0" />
      </template>
    </a-modal>

    <!-- SQL 全文弹窗：当前语句 + 完整批（含已执行过的拿锁语句） -->
    <a-modal v-model:open="sqlOpen" title="SQL 全文" :width="800" :footer="null">
      <template v-if="sqlFull">
        <div style="font-weight: 600; margin-bottom: 4px">当前执行语句</div>
        <SqlText :text="sqlFull" :max-height="320" />
      </template>
      <template v-if="batchFull && batchFull !== sqlFull">
        <div style="font-weight: 600; margin: 12px 0 4px">完整批处理（含已执行语句）</div>
        <SqlText :text="batchFull" :max-height="320" />
      </template>
    </a-modal>
  </div>
</template>

<style scoped>
/* 统计卡三列网格（历史阻塞 / 实时总览共用） */
.stat-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 12px;
  margin-bottom: 12px;
}

.forest {
  display: flex;
  flex-direction: column;
  gap: 20px;
}
.tree {
  padding: 12px;
  background: var(--bg-body);
  border-radius: 8px;
}
.tree-head {
  display: flex;
  justify-content: flex-end;
  margin-bottom: -4px;
}
.chain-chart {
  height: 300px;
  margin-top: 8px;
  border: 1px solid var(--border-color-split);
  border-radius: 8px;
  background: var(--panel-bg);
}
</style>
