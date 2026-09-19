<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import dayjs from 'dayjs'
import { getMetricsTrend, type MetricsTrend } from '../../api/metrics'
import { getDeadlocks } from '../../api/deadlock'
import { getSlowSqlList } from '../../api/slowsql'
import { getPlanChanges } from '../../api/topSql'
import { CHART_COLORS } from '../../charts/echarts'
import { useGlobalInstance } from '../../composables/useGlobalInstance'
import { useCharts } from '../../composables/useChart'
import { useTimeRange } from '../../composables/useTimeRange'
import { fmtNum, fmtTime } from '../../utils/format'
import { palette } from '../../theme/palette'
import { CAP, useEngineCaps } from '../../api/engine'

// 实例来自顶栏全局上下文：首载/切换 → 重查趋势
const { instanceId, init } = useGlobalInstance(() => search())
const { cap } = useEngineCaps()
const loading = ref(false)
const trend = ref<MetricsTrend>()

/** 时间档位：覆盖 30 天保留期（默认最近 1 小时，档位↔自定义联动统一走 useTimeRange） */
const {
  rangeKey, rangeOptions, customRange, onPresetChange, onCustomChange, fromTo, disabledDate,
} = useTimeRange({
  defaultKey: '1h',
  presets: [
    { value: '5m', label: '最近 5 分钟', minutes: 5 },
    { value: '30m', label: '最近 30 分钟', minutes: 30 },
    { value: '1h', label: '最近 1 小时', minutes: 60 },
    { value: '6h', label: '最近 6 小时', minutes: 360 },
    { value: '24h', label: '最近 24 小时', minutes: 1440 },
    { value: '3d', label: '最近 3 天', minutes: 4320 },
    { value: '7d', label: '最近 7 天', minutes: 10080 },
    { value: '30d', label: '最近 30 天', minutes: 43200 },
  ],
  onApply: () => search(),
})

/** 粒度：auto = 服务端按跨度自适应（≥7 天→5min、≥3 天→2min、≥12h→60s、≥2h→30s、其余→10s） */
const granularity = ref<'auto' | number>('auto')
const granularityOptions = [
  { value: 'auto', label: '自动' },
  { value: 10, label: '10 秒' },
  { value: 30, label: '30 秒' },
  { value: 60, label: '1 分钟' },
]

const metricsExpanded = ref(true)

// ---------- 指标卡片定义（对齐 doc/需求UI/性能趋势.png：双列网格 + 底部彩点图例） ----------
// capKey：能力矩阵键——无对等数据源的引擎（MySQL/PostgreSQL 的 OS 级 CPU/内存、PLE、编译计数、阻塞计数器）
// 整卡/单线隐藏（引擎切换随 computed 自动增删；能力边界见各 Provider 指标映射注释）
interface MetricLine { field: keyof MetricsTrend; name: string; color: string; capKey?: string }
interface MetricCard { key: string; title: string; max?: number; lines: MetricLine[]; capKey?: string }

const allCards: MetricCard[] = [
  {
    key: 'cpu', title: 'CPU 利用率（%）', max: 100, capKey: CAP.osCpuMem, lines: [
      { field: 'cpuUsagePct', name: 'CPU 利用率', color: CHART_COLORS.cyan },
    ],
  },
  {
    key: 'mem', title: '内存利用率（%）', max: 100, capKey: CAP.osCpuMem, lines: [
      { field: 'memUsagePct', name: '内存利用率', color: CHART_COLORS.purple },
    ],
  },
  {
    key: 'qps-tps', title: 'QPS / TPS', lines: [
      { field: 'qps', name: 'QPS', color: CHART_COLORS.primary },
      { field: 'tps', name: 'TPS', color: CHART_COLORS.warning },
    ],
  },
  {
    key: 'iops', title: 'IOPS（读 / 写）', lines: [
      { field: 'iopsRead', name: '读 IOPS', color: CHART_COLORS.cyan },
      { field: 'iopsWrite', name: '写 IOPS', color: CHART_COLORS.purple },
    ],
  },
  {
    key: 'mbps', title: '磁盘吞吐 MB/s（读 / 写）', lines: [
      { field: 'mbpsRead', name: '读吞吐', color: CHART_COLORS.success },
      { field: 'mbpsWrite', name: '写吞吐', color: CHART_COLORS.warning },
    ],
  },
  {
    key: 'conn', title: '连接数 / 阻塞进程', lines: [
      { field: 'userConnections', name: '连接数', color: CHART_COLORS.primary },
      { field: 'blockedProcesses', name: '阻塞进程', color: CHART_COLORS.danger, capKey: CAP.blockedProcesses },
    ],
  },
  {
    key: 'ple', title: 'PLE 页平均生存期（秒）', capKey: CAP.ple, lines: [
      { field: 'ple', name: 'PLE', color: CHART_COLORS.purple },
    ],
  },
  {
    key: 'hit', title: '缓存命中率（%）', max: 100, lines: [
      { field: 'bufferCacheHitRatioPct', name: 'Buffer 命中率', color: CHART_COLORS.cyan },
    ],
  },
  {
    key: 'compile', title: '编译 / 重编译（次/秒）', capKey: CAP.compileStats, lines: [
      { field: 'compilationsPerSec', name: '编译', color: CHART_COLORS.primary },
      { field: 'recompilationsPerSec', name: '重编译', color: CHART_COLORS.warning },
    ],
  },
  {
    key: 'deadlock', title: '死锁（次/秒）', lines: [
      { field: 'deadlocksPerSec', name: '死锁', color: CHART_COLORS.danger },
    ],
  },
  {
    key: 'lock-timeout', title: '锁超时（次/秒）', lines: [
      { field: 'lockTimeoutsPerSec', name: '锁超时', color: CHART_COLORS.warning },
    ],
  },
]

/** 当前实例可见卡片：无对等数据源能力的整卡与单线隐藏 */
const cards = computed(() => {
  return allCards
    .filter(c => !c.capKey || cap(c.capKey) !== 'none')
    .map(c => ({ ...c, lines: c.lines.filter(l => !l.capKey || cap(l.capKey) !== 'none') }))
    .filter(c => c.lines.length > 0)
})

/** 有卡片/单线被能力过滤时分组头给提示（无隐藏不显示） */
const hiddenNote = computed(() => allCards.some(c =>
  (c.capKey != null && cap(c.capKey) === 'none') || c.lines.some(l => l.capKey != null && cap(l.capKey) === 'none')))

/** X 轴标签：跨度超 1 天（或桶 ≥1 小时）带日期；10/30s 桶带秒；否则仅时分 */
function xFormat(bucketSeconds: number, times: string[]) {
  if (bucketSeconds >= 3600 || times.length * bucketSeconds > 86400) return 'MM-DD HH:mm'
  return bucketSeconds <= 30 ? 'HH:mm:ss' : 'HH:mm'
}

const xLabels = computed(() => {
  const t = trend.value
  if (!t) return []
  const fmt = xFormat(t.bucketSeconds, t.times)
  return t.times.map(x => dayjs(x).format(fmt))
})

/** 数据新鲜度：末桶时间（采集停摆/实例下线时图变平或断线，标注让"数据旧"一眼可辨） */
const lastDataTime = computed(() => {
  const times = trend.value?.times
  return times?.length ? times[times.length - 1] : undefined
})

// ---------- 事件叠加（markLine：死锁/慢SQL/计划变更时刻，指标异常拐点对齐定位） ----------
/** 事件色：死锁=红 / 慢SQL=金黄 / 计划变更=青（慢SQL 用 gold：比 warning 橙更远离 danger 红） */
const EVENT_COLORS = {
  deadlock: CHART_COLORS.danger,
  slowsql: palette.brand.gold,
  plan: CHART_COLORS.cyan,
} as const
/** 事件线型：颜色之外再加一层形状区分（色弱也可辨） */
const EVENT_LINE_TYPES = { deadlock: 'dashed', slowsql: 'dotted', plan: 'dashed' } as const
const EVENT_LABELS = { deadlock: '死锁', slowsql: '慢SQL', plan: '计划变更' } as const
type EventKind = keyof typeof EVENT_COLORS

const showEvents = ref(false)
const events = ref<{ time: string; kind: EventKind }[]>([])

/** 事件 → 桶序号分组（事件时间落在的桶 = 最后一个 ≤ 它的桶；窗口外丢弃）。
 *  markLine 虚线与 tooltip 事件行同源，保证"线在哪、悬停提示就在哪" */
function eventBuckets(): Map<number, { kind: EventKind; time: string }[]> {
  const map = new Map<number, { kind: EventKind; time: string }[]>()
  const t = trend.value
  if (!t?.times.length || !events.value.length) return map
  const times = t.times.map(x => dayjs(x).valueOf())
  for (const e of events.value) {
    const ms = dayjs(e.time).valueOf()
    let idx = times.findIndex(x => x > ms) - 1
    if (idx === -2) idx = times.length - 1   // 晚于末桶：钉在末桶
    if (idx < 0) continue                    // 早于首桶：窗口外
    const list = map.get(idx) ?? []
    list.push(e)
    map.set(idx, list)
  }
  return map
}

function eventMarkLine(buckets: Map<number, { kind: EventKind; time: string }[]>) {
  const data = [...buckets.entries()].flatMap(([idx, list]) =>
    list.map(e => ({ xAxis: idx, lineStyle: { color: EVENT_COLORS[e.kind], type: EVENT_LINE_TYPES[e.kind] } })))
  if (!data.length) return null
  return {
    silent: true,
    symbol: 'none',
    lineStyle: { type: 'dashed' as const, width: 1 },
    label: { show: false },
    data,
  }
}

/** 坐标轴 tooltip：常规系列值行下追加当前桶事件行（悬停虚线所在列即见事件类型与时刻；单桶超 5 条折叠计数） */
function eventTooltip(params: unknown, buckets: Map<number, { kind: EventKind; time: string }[]>) {
  const list = (Array.isArray(params) ? params : [params]) as {
    dataIndex?: number
    axisValueLabel?: string
    name?: string
    marker?: string
    seriesName?: string
    value?: number | null
  }[]
  const p0 = list[0]
  if (!p0) return ''
  let html = `${p0.axisValueLabel ?? p0.name ?? ''}<br/>`
  for (const p of list) {
    if (p.value == null) continue
    html += `${p.marker ?? ''} ${p.seriesName}：${fmtNum(p.value, 2)}<br/>`
  }
  const evs = buckets.get(p0.dataIndex ?? -1)
  if (evs?.length) {
    const rows = evs.slice(0, 5).map(e =>
      `<div style="color:${EVENT_COLORS[e.kind]}">▸ ${EVENT_LABELS[e.kind]} ${dayjs(e.time).format('MM-DD HH:mm:ss')}</div>`).join('')
    const more = evs.length > 5 ? `<div>…等 ${evs.length} 个事件</div>` : ''
    html += `<div style="border-top:1px solid ${palette.border.split};margin-top:4px;padding-top:2px">${rows}${more}</div>`
  }
  return html
}

/** 各事件源按引擎能力取数（页大小上限内取首页；叠加失败不挡主图） */
async function loadEvents(from: string, to: string) {
  const id = instanceId.value
  if (!showEvents.value || id === undefined) {
    events.value = []
    return
  }
  const jobs: Promise<{ time: string; kind: EventKind }[]>[] = []
  if (cap(CAP.deadlockEvents) !== 'none')
    jobs.push(getDeadlocks(id, { from, to, limit: 100 })
      .then(r => r.items.map(i => ({ time: i.eventTimeUtc, kind: 'deadlock' as const }))))
  if (cap(CAP.slowSqlEvents) !== 'none')
    jobs.push(getSlowSqlList(id, { from, to, limit: 100 })
      .then(r => r.items.map(i => ({ time: i.eventTimeUtc, kind: 'slowsql' as const }))))
  if (cap(CAP.queryPlanSnapshot) !== 'none')
    jobs.push(getPlanChanges(id, { topN: 100 })
      .then(rs => rs
        .filter(e => dayjs(e.changedAtUtc).isAfter(dayjs(from)) && dayjs(e.changedAtUtc).isBefore(dayjs(to)))
        .map(e => ({ time: e.changedAtUtc, kind: 'plan' as const }))))
  try {
    events.value = (await Promise.all(jobs)).flat()
  } catch {
    events.value = []
  }
}

watch(showEvents, async v => {
  if (v) {
    const { from, to } = fromTo()
    if (from && to) await loadEvents(from, to)
  }
  else events.value = []
  renderVisible()
})

// ---------- 图表生命周期：useCharts 统一（懒渲染 + 自动 resize + keep-alive 兜底） ----------
const { bind, renderVisible } = useCharts((key) => {
  const c = cards.value.find(x => x.key === key)
  if (!c) return null
  const t = trend.value
  const buckets = eventBuckets()
  return {
    tooltip: { trigger: 'axis', formatter: (params: unknown) => eventTooltip(params, buckets) },
    legend: { bottom: 0, icon: 'circle', itemWidth: 8, itemHeight: 8, itemGap: 16, textStyle: { fontSize: 12 } },
    grid: { left: 8, right: 12, top: 16, bottom: 28, containLabel: true },
    xAxis: {
      type: 'category',
      data: xLabels.value,
      boundaryGap: false,
      axisLabel: { hideOverlap: true },
      axisTick: { show: false },
    },
    yAxis: { type: 'value', max: c.max },
    series: c.lines.map((l, i) => ({
      name: l.name,
      type: 'line' as const,
      data: (t?.[l.field] ?? []) as (number | null)[],
      smooth: true,
      showSymbol: false,
      connectNulls: false,   // 空桶 null 形成断点（实例重启/采集中断）
      lineStyle: { width: 2, color: l.color },
      itemStyle: { color: l.color },
      // 事件时刻竖虚线只挂第一个系列（全局生效）
      ...(i === 0 ? { markLine: eventMarkLine(buckets) ?? {} } : {}),
    })),
  }
})

async function search() {
  if (instanceId.value === undefined) return
  const { from, to } = fromTo()
  if (!from || !to) {
    trend.value = undefined
    return
  }
  loading.value = true
  try {
    trend.value = await getMetricsTrend(instanceId.value, {
      from, to,
      ...(granularity.value !== 'auto' ? { bucketSeconds: Number(granularity.value) } : {}),
    })
    // 事件与趋势同窗加载到位后再重绘（内部自吞错不挡主图；不等它，首屏图先画完、
    // 事件后到没人触发重绘 → 虚线永远不出现）
    await loadEvents(from, to)
    // nextTick 等卡片容器挂载后再重绘（首次进入视口由 IO 兜底）
    requestAnimationFrame(() => renderVisible())
  } finally {
    loading.value = false
  }
}

onMounted(init)
</script>

<template>
  <div class="page">
    <a-card>
      <!-- 实例在顶栏全局选择，页内不再重复 -->
      <div class="toolbar" style="margin-bottom: 16px">
        <span>时间：</span>
        <a-select :value="rangeKey" style="width: 150px" :options="rangeOptions" @change="onPresetChange" />
        <a-range-picker
          v-model:value="customRange"
          show-time
          format="YYYY-MM-DD HH:mm"
          :placeholder="['开始时间', '结束时间']"
          :disabled-date="disabledDate"
          @change="onCustomChange"
        />
        <span>粒度：</span>
        <a-select v-model:value="granularity" style="width: 100px" :options="granularityOptions" @change="search" />
        <a-button type="primary" :loading="loading" @click="search">刷新</a-button>
        <a-tooltip title="竖线标注事件时刻（各取窗口内前 100 条）：红虚线=死锁 金黄点线=慢SQL 青虚线=计划变更；悬停图中事件列可看类型与时刻">
          <a-checkbox v-model:checked="showEvents">叠加事件</a-checkbox>
        </a-tooltip>
        <span v-if="lastDataTime" class="toolbar-right text-tertiary">数据截至 {{ fmtTime(lastDataTime) }}</span>
      </div>

      <!-- 可折叠"基础指标"分组头（灰底圆角，点击收起/展开） -->
      <div class="group-header" @click="metricsExpanded = !metricsExpanded">
        <span class="toggle-arrow" :class="{ collapsed: !metricsExpanded }"></span>
        <span>基础指标</span>
        <span v-if="hiddenNote" class="text-tertiary" style="font-weight: 400; margin-left: 8px">
          该引擎无 OS 级 CPU/内存、PLE、编译计数等部分对等数据源，对应卡片/曲线已隐藏
        </span>
      </div>

      <!-- 双列卡片网格（窄屏单列）；图表容器常驻（v-show），滚动到可视区才渲染 -->
      <div v-show="metricsExpanded" class="card-grid">
        <div v-for="c in cards" :key="c.key" class="metric-card">
          <div class="metric-title">{{ c.title }}</div>
          <div :ref="bind(c.key)" class="metric-chart"></div>
        </div>
      </div>

      <a-empty
        v-if="!loading && instanceId !== undefined && (trend?.times.length ?? 0) === 0"
        description="所选时间范围内暂无采样数据"
        style="padding: 32px 0"
      />
    </a-card>
  </div>
</template>

<style scoped>
.group-header {
  display: flex;
  align-items: center;
  gap: 6px;
  background: var(--bg-body);
  border-radius: 4px;
  padding: 8px 16px;
  cursor: pointer;
  color: var(--text-secondary);
  font-weight: 600;
  user-select: none;
  margin-bottom: 12px;
}
/* 折叠箭头：CSS 三角，收起时左转 90° */
.toggle-arrow {
  width: 0;
  height: 0;
  border-left: 5px solid transparent;
  border-right: 5px solid transparent;
  border-top: 6px solid var(--text-tertiary);
  transition: transform 0.2s;
}
.toggle-arrow.collapsed {
  transform: rotate(-90deg);
}
.card-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 12px;
}
@media (max-width: 1100px) {
  .card-grid {
    grid-template-columns: 1fr;
  }
}
.metric-card {
  background: var(--panel-bg);
  border: 1px solid var(--border-color-split);
  border-radius: 4px;
  padding: 12px;
}
.metric-title {
  font-size: 12px;
  font-weight: 600;
  color: var(--text-secondary);
  margin-bottom: 4px;
}
.metric-chart {
  height: 220px;
}
</style>
