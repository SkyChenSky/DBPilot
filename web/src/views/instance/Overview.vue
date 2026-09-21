<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import dayjs from 'dayjs'
import { getMetricsTrend, type MetricsTrend } from '../../api/metrics'
import { getDeadlocks } from '../../api/deadlock'
import { getBlockingEvents, getBlockingStats, type BlockingStats } from '../../api/blocking'
import { getSlowSqlList } from '../../api/slowsql'
import { getPlanChanges, getTopSqlHistory, type TopSqlHistoryItem } from '../../api/topSql'
import { engineBadgeColor, engineLabel, versionLabel, type InstanceItem } from '../../api/instance'
import { CAP, useEngineCaps } from '../../api/engine'
import { CHART_COLORS } from '../../charts/echarts'
import { useGlobalInstance } from '../../composables/useGlobalInstance'
import { useCharts } from '../../composables/useChart'
import { fmtMsUnit, fmtNum, fmtTime, fmtTimeMinute } from '../../utils/format'

/**
 * 实例概览（首页仪表盘）：当前顶栏实例的信息头 + 近 1h 关键指标卡（数值 + 峰值均值 + 迷你趋势）
 * + 近 24h 异常时间线 + Top SQL 迷你榜。数据全部复用既有 API，无新后端面；
 * 引擎差异由能力矩阵减法（CPU/内存卡、阻塞线、事件源）。
 */

const router = useRouter()
const { cap, capsReady } = useEngineCaps()
const { instances, instanceId, init } = useGlobalInstance(() => load())

const current = computed<InstanceItem | undefined>(() =>
  instances.value.find(i => i.id === instanceId.value))

const STATUS_META: Record<number, { label: string; color: string }> = {
  0: { label: '未知', color: 'default' },
  1: { label: '在线', color: 'success' },
  2: { label: '退避中', color: 'warning' },
  3: { label: '离线', color: 'error' },
}
const statusMeta = computed(() => STATUS_META[current.value?.status ?? 0])

// ---------- 关键指标卡（近 1h 趋势：末值 + 峰值/均值 + 迷你趋势线） ----------
const loading = ref(false)
const trend = ref<MetricsTrend>()
const lastDataTime = ref<string>()

/** 序列最后非空值（采集停摆时拿到的是末个有效样本，配合"数据截至"判新鲜度） */
function lastVal(arr?: (number | null)[]) {
  for (let i = (arr?.length ?? 0) - 1; i >= 0; i--)
    if (arr![i] != null) return arr![i]!
  return undefined
}

type Tone = 'primary' | 'success' | 'warning' | 'danger'
const TONE_COLOR: Record<Tone, string> = {
  primary: CHART_COLORS.primary,
  success: CHART_COLORS.success,
  warning: CHART_COLORS.warning,
  danger: CHART_COLORS.danger,
}
interface CardDef { key: string; title: string; field: keyof MetricsTrend; tone?: Tone; capKey?: string }

const allCards: CardDef[] = [
  { key: 'qps', title: 'QPS', field: 'qps', tone: 'primary' },
  { key: 'tps', title: 'TPS', field: 'tps' },
  { key: 'conn', title: '连接数', field: 'userConnections' },
  { key: 'cpu', title: 'CPU 利用率（%）', field: 'cpuUsagePct', capKey: CAP.osCpuMem },
  { key: 'mem', title: '内存利用率（%）', field: 'memUsagePct', capKey: CAP.osCpuMem },
  { key: 'hit', title: '缓存命中率（%）', field: 'bufferCacheHitRatioPct', tone: 'success' },
  { key: 'blocked', title: '阻塞进程数', field: 'blockedProcesses', capKey: CAP.blockedProcesses, tone: 'warning' },
  { key: 'deadlock', title: '死锁（次/秒）', field: 'deadlocksPerSec', tone: 'danger' },
]

const cards = computed(() => allCards
  .filter(c => !c.capKey || cap(c.capKey) !== 'none')
  .map(c => {
    const arr = (trend.value?.[c.field] ?? []) as (number | null)[]
    const vals = arr.filter((v): v is number => v != null)
    return {
      ...c,
      value: lastVal(arr),
      peak: vals.length ? Math.max(...vals) : undefined,
      avg: vals.length ? vals.reduce((a, b) => a + b, 0) / vals.length : undefined,
      color: TONE_COLOR[c.tone ?? 'primary'],
    }
  }))

/** 迷你趋势线：无轴无线框的极简折线（数据来自同一份 1h 趋势，不额外发请求） */
const { bind: bindSpark, renderVisible } = useCharts((key) => {
  const c = cards.value.find(x => x.key === key)
  if (!c || !trend.value?.times.length) return null
  return {
    animation: false,
    grid: { left: 0, right: 0, top: 4, bottom: 0 },
    xAxis: { type: 'category', show: false, boundaryGap: false, data: trend.value.times },
    yAxis: { type: 'value', show: false },
    series: [{
      type: 'line' as const,
      data: (trend.value[c.field] ?? []) as (number | null)[],
      smooth: true,
      showSymbol: false,
      connectNulls: false,
      lineStyle: { width: 1.5, color: c.color },
      itemStyle: { color: c.color },
      areaStyle: { opacity: 0.08, color: c.color },
    }],
  }
})

// ---------- 近 24h 异常时间线（死锁/慢SQL/计划变更/阻塞，按能力取源） ----------
interface TimelineEntry {
  time: string
  kind: 'deadlock' | 'slowsql' | 'plan' | 'blocking'
  text: string
  route: string
}
const KIND_COLOR = {
  deadlock: CHART_COLORS.danger,
  slowsql: CHART_COLORS.warning,
  plan: CHART_COLORS.cyan,
  blocking: CHART_COLORS.primary,
} as const
const KIND_LABEL = { deadlock: '死锁', slowsql: '慢SQL', plan: '计划变更', blocking: '阻塞' } as const

const events = ref<TimelineEntry[]>([])
const eventCounts = ref<Record<TimelineEntry['kind'], number>>({ deadlock: 0, slowsql: 0, plan: 0, blocking: 0 })
const blockingStats = ref<BlockingStats>()

const from24h = () => dayjs().subtract(24, 'hour').toISOString()
const toNow = () => dayjs().toISOString()

async function loadEvents() {
  const id = instanceId.value!
  const from = from24h()
  const to = toNow()
  const jobs: Promise<TimelineEntry[]>[] = []
  // 死锁 / 慢SQL / 计划变更：与趋势页事件叠加同源同能力门
  if (cap(CAP.deadlockEvents) !== 'none')
    jobs.push(getDeadlocks(id, { from, to, limit: 10 })
      .then(r => {
        eventCounts.value.deadlock = r.total
        return r.items.map(i => ({
          time: i.eventTimeUtc, kind: 'deadlock' as const, route: '/lock/deadlocks',
          text: `牺牲进程 ${i.victimSpids} ｜ ${i.objects}`,
        }))
      }))
  if (cap(CAP.slowSqlEvents) !== 'none')
    jobs.push(getSlowSqlList(id, { from, to, limit: 10 })
      .then(r => {
        eventCounts.value.slowsql = r.total
        return r.items.map(i => ({
          time: i.eventTimeUtc, kind: 'slowsql' as const, route: '/slowlog',
          text: `耗时 ${fmtMsUnit(i.durationMs)} ｜ ${(i.sqlPreview || '(空)').slice(0, 60)}`,
        }))
      }))
  if (cap(CAP.queryPlanSnapshot) !== 'none')
    jobs.push(getPlanChanges(id, { topN: 100 })
      .then(rs => {
        const win = rs.filter(e => dayjs(e.changedAtUtc).isAfter(dayjs(from)))
        eventCounts.value.plan = win.length
        return win.map(e => ({
          time: e.changedAtUtc, kind: 'plan' as const, route: '/performance/topsql',
          text: `${fmtMsUnit(e.oldAvgElapsedMs)} → ${fmtMsUnit(e.newAvgElapsedMs)} ｜ 指纹 ${e.fingerprint.slice(0, 8)}…`,
        }))
      }))
  // 阻塞事件引擎无关（三引擎都有会话/阻塞采集）
  jobs.push(getBlockingEvents(id, { from, to, limit: 10 })
    .then(r => {
      eventCounts.value.blocking = r.total
      return r.items.map(i => ({
        time: i.startTimeUtc, kind: 'blocking' as const, route: '/lock/blocking',
        text: `阻塞 ${i.blockedCount} 会话 ｜ 最长等待 ${i.maxWaitSeconds}s${i.headInfo ? ` ｜ ${i.headInfo}` : ''}`,
      }))
    }))
  const results = await Promise.allSettled(jobs)
  events.value = results.flatMap(r => (r.status === 'fulfilled' ? r.value : []))
    .sort((a, b) => dayjs(b.time).valueOf() - dayjs(a.time).valueOf())
    .slice(0, 10)
}

// ---------- Top SQL 迷你榜（历史差值总榜 Top5，引擎无关；耗时条按榜内最大值比例） ----------
const topSql = ref<TopSqlHistoryItem[]>()

async function loadTopSql() {
  topSql.value = (await getTopSqlHistory(instanceId.value!, {
    db: '', metric: 'total', topN: 5, excludeSystemDb: true,
  })).items
}

const maxElapsed = computed(() => Math.max(1, ...(topSql.value ?? []).map(r => r.totalElapsedMs)))

// ---------- 加载编排 ----------
// 能力矩阵未就位时 cap() 全 full 兜底会打错降级端点：先跳过，ready 翻转后补查（DeadlockList 同款）
async function load() {
  if (instanceId.value === undefined || !capsReady.value) return
  loading.value = true
  try {
    const r = await getMetricsTrend(instanceId.value, { from: dayjs().subtract(1, 'hour').toISOString(), to: toNow() })
    trend.value = r
    lastDataTime.value = r.times.length ? r.times[r.times.length - 1] : undefined
    requestAnimationFrame(() => renderVisible())
    // 三路独立容错：单源失败不拖垮整页仪表盘
    loadEvents().catch(() => { events.value = [] })
    loadTopSql().catch(() => { topSql.value = [] })
    getBlockingStats(instanceId.value).then(s => blockingStats.value = s).catch(() => {})
  } finally {
    loading.value = false
  }
}

watch(capsReady, v => v && load())

function go(route: string) {
  router.push(route)
}

onMounted(init)
</script>

<template>
  <div class="page">
    <!-- 实例信息头 -->
    <a-card>
      <template v-if="current">
        <div class="inst-head">
          <div class="inst-title">
            <span class="inst-name">{{ current.name }}</span>
            <a-tag :color="engineBadgeColor(current.engine)">{{ engineLabel(current.engine) }}</a-tag>
            <a-tag :color="statusMeta.color">{{ statusMeta.label }}</a-tag>
            <span class="text-tertiary">{{ versionLabel(current.majorVersion, current.engine) }} ｜ {{ current.host }}:{{ current.port }}</span>
          </div>
        </div>
        <div class="inst-meta text-tertiary" v-if="current.machineName || current.cpuCores || current.lastHeartbeat || blockingStats">
          <span v-if="current.machineName">主机 {{ current.machineName }}</span>
          <span v-if="current.cpuCores">{{ current.cpuCores }} 核</span>
          <span v-if="current.lastHeartbeat">心跳 {{ fmtTimeMinute(current.lastHeartbeat) }}</span>
          <span v-if="blockingStats">近一天阻塞 {{ blockingStats.dayCount }} 次</span>
        </div>
        <a-alert
          v-if="current.lastError"
          type="warning"
          show-icon
          :message="current.lastError"
          style="margin-top: 8px"
        />
      </template>
      <a-empty
        v-else-if="!loading"
        description="暂无实例：请先在实例管理中接入"
        style="padding: 48px 0"
      >
        <a-button type="primary" @click="go('/instance')">前往实例管理</a-button>
      </a-empty>
    </a-card>

    <template v-if="current">
      <!-- 关键指标：数值 + 峰值均值 + 迷你趋势线 -->
      <a-card>
        <div class="sec-head">
          <span class="sec-title">关键指标</span>
          <span class="text-tertiary" v-if="lastDataTime">近 1 小时 ｜ 数据截至 {{ fmtTime(lastDataTime) }}</span>
          <a-button size="small" class="sec-refresh" :loading="loading" @click="load">刷新</a-button>
        </div>
        <div class="card-grid">
          <div v-for="c in cards" :key="c.key" class="m-card">
            <div class="m-title">{{ c.title }}</div>
            <div class="m-value">{{ c.value != null ? fmtNum(c.value, 1) : '—' }}</div>
            <div class="m-sub text-tertiary">
              <template v-if="c.peak != null">峰值 {{ fmtNum(c.peak, 1) }} ｜ 均值 {{ fmtNum(c.avg!, 1) }}</template>
              <template v-else>{{ loading ? '加载中…' : '暂无采样' }}</template>
            </div>
            <div :ref="bindSpark(c.key)" class="m-spark"></div>
          </div>
        </div>
      </a-card>

      <!-- 异常时间线 + Top SQL 迷你榜 -->
      <div class="two-col">
        <a-card title="近 24 小时异常事件">
          <div class="event-summary text-tertiary" v-if="events.length">
            <span>死锁 {{ eventCounts.deadlock }}</span>
            <span>慢SQL {{ eventCounts.slowsql }}</span>
            <span>计划变更 {{ eventCounts.plan }}</span>
            <span>阻塞 {{ eventCounts.blocking }}</span>
          </div>
          <a-timeline v-if="events.length" style="margin-top: 12px">
            <a-timeline-item v-for="(e, i) in events" :key="i" :color="KIND_COLOR[e.kind]">
              <div class="event-item" @click="go(e.route)">
                <span class="event-time">{{ fmtTime(e.time) }}</span>
                <a-tag :color="KIND_COLOR[e.kind]" class="event-tag">{{ KIND_LABEL[e.kind] }}</a-tag>
                <span class="event-text">{{ e.text }}</span>
              </div>
            </a-timeline-item>
          </a-timeline>
          <a-empty v-else description="近 24 小时无异常事件" :image-style="{ height: '48px' }" style="padding: 24px 0" />
        </a-card>

        <a-card title="Top SQL（按总耗时 Top 5）">
          <a @click="go('/performance/topsql')" style="position: absolute; right: 24px; top: 16px">查看全部</a>
          <div v-if="topSql?.length" class="sql-list">
            <div v-for="(r, i) in topSql" :key="r.fingerprint" class="sql-row" @click="go('/performance/topsql')">
              <span class="rank" :class="{ top: i < 3 }">{{ i + 1 }}</span>
              <div class="sql-main">
                <div class="sql-text" :title="r.sqlText || `指纹 ${r.fingerprint}`">{{ (r.sqlText || `（指纹 ${r.fingerprint.slice(0, 8)}…）`).slice(0, 120) }}</div>
                <div class="numcell sql-elapsed">
                  <div class="numbar" :style="{ width: `${Math.max(4, Math.round(r.totalElapsedMs / maxElapsed * 100))}%` }"></div>
                  <span>执行 {{ fmtNum(r.executionCount) }} 次 ｜ 总耗时 {{ fmtMsUnit(r.totalElapsedMs) }}</span>
                </div>
              </div>
            </div>
          </div>
          <a-empty v-else description="暂无差值数据（采集运行数分钟后生成）" :image-style="{ height: '48px' }" style="padding: 24px 0" />
        </a-card>
      </div>
    </template>
  </div>
</template>

<style scoped>
.inst-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 4px;
}
.inst-title {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}
.inst-name {
  font-size: 16px;
  font-weight: 600;
}
.inst-meta {
  display: flex;
  gap: 16px;
  font-size: 12px;
}
.sec-head {
  display: flex;
  align-items: baseline;
  gap: 12px;
  margin-bottom: 12px;
}
.sec-title {
  font-weight: 600;
}
/* 刷新按钮推到标题行右端 */
.sec-refresh {
  margin-left: auto;
}
.card-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 12px;
}
@media (max-width: 1100px) {
  .card-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
}
.m-card {
  background: var(--panel-bg);
  border: 1px solid var(--border-color-split);
  border-radius: 4px;
  padding: 12px 14px;
  min-width: 0;
}
.m-title {
  font-size: 12px;
  color: var(--text-secondary);
  margin-bottom: 4px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.m-value {
  font-size: 24px;
  font-weight: 600;
  line-height: 1.2;
  font-variant-numeric: tabular-nums;
}
.m-sub {
  font-size: 11px;
  margin: 2px 0 6px;
}
.m-spark {
  height: 40px;
}
.two-col {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  gap: 16px;
  align-items: start;
}
@media (max-width: 1100px) {
  .two-col { grid-template-columns: 1fr; }
}
.event-summary {
  display: flex;
  gap: 16px;
  font-size: 12px;
}
.event-item {
  display: flex;
  align-items: baseline;
  gap: 8px;
  cursor: pointer;
  min-width: 0;
}
.event-item:hover .event-text {
  color: var(--color-primary);
}
.event-time {
  flex-shrink: 0;
  font-size: 12px;
  color: var(--text-tertiary);
  font-variant-numeric: tabular-nums;
}
.event-tag {
  flex-shrink: 0;
  margin-inline-end: 0;
  font-size: 11px;
  line-height: 18px;
}
.event-text {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  color: var(--text-secondary);
}
.sql-list {
  display: flex;
  flex-direction: column;
}
.sql-row {
  display: flex;
  gap: 10px;
  padding: 8px 0;
  border-bottom: 1px solid var(--border-color-split);
  cursor: pointer;
  align-items: flex-start;
}
.sql-row:last-child {
  border-bottom: none;
}
.sql-row:hover .sql-text {
  color: var(--color-primary);
}
.rank {
  flex-shrink: 0;
  width: 20px;
  height: 20px;
  border-radius: 50%;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  font-size: 11px;
  color: var(--text-tertiary);
  border: 1px solid var(--border-color-split);
  margin-top: 1px;
}
.rank.top {
  color: #fff;
  background: var(--color-primary);
  border-color: var(--color-primary);
}
.sql-main {
  flex: 1;
  min-width: 0;
}
.sql-text {
  font-family: consolas, monospace;
  font-size: 12px;
  color: var(--text-secondary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  margin-bottom: 2px;
}
.sql-elapsed {
  font-size: 11px;
  color: var(--text-tertiary);
}
</style>
