<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import dayjs from 'dayjs'
import {
  getAasHistory, getAasRealtime, getSqlTrend, getTopSql,
  type AasPoint, type BucketNames, type TopSqlItem,
} from '../../api/insight'
import { CHART_COLORS, CHART_SERIES } from '../../charts/echarts'
import SqlText from '../../components/SqlText.vue'
import { useGlobalInstance } from '../../composables/useGlobalInstance'
import { usePolling } from '../../composables/usePolling'
import { useTimeRange } from '../../composables/useTimeRange'
import { useCharts } from '../../composables/useChart'

/** 范围档位：<1h（5m/30m）走内存缓冲 10s 瞬时值（对齐阿里云）；≥1h 走分钟均值表；custom 自定义 */

/** AAS 分类维度（对齐阿里云七维）：Waits = 等待类别（锁/IO/网络/内存/并行等，原生 wait_type 归并） */
type DimKey = 'wait' | 'sql' | 'user' | 'host' | 'command' | 'db' | 'status'

const dimApiName: Record<DimKey, string> = {
  wait: 'wait', sql: 'sql', user: 'user', host: 'host', command: 'command', db: 'db', status: 'status',
}
const dimLabel: Record<DimKey, string> = {
  wait: 'Waits', sql: 'SQL', user: 'Users', host: 'Hosts',
  command: 'Commands', db: 'Databases', status: 'Status',
}
/** 非自定义维度的堆叠系列数上限（其余合并"其他"，避免图例爆炸） */
const maxDimSeries = 7

/** 展示层桶（重要性排序，userWait=WAITFOR 计活跃，other 恒殿后）：logWrite+sysIo 合并为 IO */
const aasBucketOrder = ['cpu', 'lock', 'io', 'userIo', 'network', 'memory', 'parallel',
  'bufferLatch', 'latch', 'threads', 'backup', 'hadr', 'trace', 'broker', 'fullText', 'preemptive', 'userWait', 'other']
/** 桶色：全站轮转色板按序取色（超出循环），与非 Waits 维度的系列自动取色同源 */
const colorOf = (k: string) => {
  const i = aasBucketOrder.indexOf(k)
  return i >= 0 ? CHART_SERIES[i % CHART_SERIES.length] : undefined
}
/** 展示桶取值：io = logWrite + sysIo 合并，其余直取 */
const bucketVal = (p: AasPoint, k: string): number =>
  k === 'io' ? (p.buckets?.logWrite ?? 0) + (p.buckets?.sysIo ?? 0) : (p.buckets?.[k] ?? 0)
/** 展示名：io=IO 合并桶；其余直接用桶 key（userWait 即原生 key） */
const displayKey = (k: string) => (k === 'io' ? 'IO' : k)
/** SQL 的 AAS 构成（后端原始桶）→ 展示桶：logWrite+sysIo 合并为 io，降序 */
function sqlBreakdown(row: TopSqlItem): [string, number][] {
  const m: Record<string, number> = {}
  for (const [k, v] of Object.entries(row.buckets ?? {})) {
    const dk = k === 'logWrite' || k === 'sysIo' ? 'io' : k
    m[dk] = (m[dk] ?? 0) + v
  }
  return Object.entries(m).filter(([, v]) => v > 0).sort((a, b) => b[1] - a[1])
}
const bucketZh = (k: string) => (k === 'io' ? 'IO（日志写+系统 IO）' : bucketNames.value[k]) ?? k
const sqlBreakdownTotal = (row: TopSqlItem) =>
  sqlBreakdown(row).reduce((s, e) => s + e[1], 0) || 1
/** 构成条总长：以当前榜单最大 AAS 为标尺（64px），行间长度直观可比 */
const barWidth = (row: TopSqlItem) => {
  const max = Math.max(...topSql.value.map(r => r.aas), 0.001)
  return Math.max(8, (row.aas / max) * 64)
}

// 实例来自顶栏全局上下文：首载/切换 → onRangeChange 重查（useGlobalInstance 内含 watch）
const { instanceId, init } = useGlobalInstance(() => onRangeChange())
/** 时间档位默认近 5 分钟（实时口径；档位↔自定义联动统一走 useTimeRange） */
const { rangeKey, rangeOptions, customRange, onPresetChange, onCustomChange, fromTo, disabledDate } = useTimeRange({ defaultKey: '5m', onApply: () => onRangeChange() })
const autoRefresh = ref(true)
const dim = ref<DimKey>('wait')

const cpuCores = ref<number>()
const bucketNames = ref<BucketNames>({})   // 桶 key → 中文名（tooltip 用；图例直接用 key）
const lastTickUtc = ref<string>()
const emptyReason = ref('')
const topSql = ref<TopSqlItem[]>([])
const topSqlLoading = ref(false)

/** <1h 用实时值（10s 瞬时计数，整数）；≥1h 用分钟均值（可能小数）—— 对齐阿里云口径 */
const isRealtime = () => rangeKey.value === '5m' || rangeKey.value === '30m'
const granularity = () => (isRealtime() ? '10 秒' : '1 分钟')

/** 当前范围的起止（UTC ISO；custom 未选完整时返回 undefined，调用方前置拦截） */
function currentRange(): { start?: string; end?: string } {
  const { from, to } = fromTo()
  return { start: from, end: to }
}

const { start: startPolling, stop: stopPolling } = usePolling(fetch, 10_000, { immediate: false })

async function fetch() {
  if (instanceId.value === undefined) return
  const { start, end } = currentRange()
  if (!start || !end) return   // 自定义未选完整：不查询（轮询静默跳过）
  try {
    if (isRealtime()) {
      const r = await getAasRealtime(instanceId.value, rangeKey.value === '30m' ? 30 : 5)
      cpuCores.value = r.cpuCores ?? undefined
      bucketNames.value = r.bucketNames ?? {}
      lastTickUtc.value = r.lastTickUtc ?? undefined
      emptyReason.value = r.points.length ? '' : (r.lastTickUtc ? '时间范围内无样本' : '采样尚未开始（等待第一个 10s 采样）')
      render(r.points)
    } else {
      const r = await getAasHistory(instanceId.value, start, end)
      cpuCores.value = r.cpuCores ?? undefined
      bucketNames.value = r.bucketNames ?? {}
      lastTickUtc.value = undefined
      emptyReason.value = r.points.length ? '' : '时间范围内无分钟聚合数据（采样运行满 1 分钟后生成）'
      render(r.points)
    }
    loadTopSql(start, end)
  } catch {
    // apiGet 已统一提示，这里吞掉防止轮询中断
  }
}

async function loadTopSql(start: string, end: string) {
  if (instanceId.value === undefined) return
  topSqlLoading.value = true
  try {
    topSql.value = await getTopSql(instanceId.value, start, end)
  } catch {
    // 吞掉：表格空态即可
  } finally {
    topSqlLoading.value = false
  }
}

/** 堆叠序列：Waits → 等待类别（原生 wait_type 归并，类别名后端下发）；其他维度 → Top N + 其他 */
function buildSeries(points: AasPoint[]) {
  if (dim.value === 'wait') {
    // 重要性排序（idle 计活跃、会话占用口径），只画区间内出现过样本的桶；
    // 无样本点补 0 而非 null —— 堆叠层之间空洞会撕裂面积图
    const keys = aasBucketOrder.filter(k => k !== 'other' && points.some(p => bucketVal(p, k) > 0))
    if (points.some(p => (p.buckets?.other ?? 0) > 0)) keys.push('other')   // other 恒殿后
    return keys.map(k => ({ name: displayKey(k), data: points.map(p => bucketVal(p, k)), color: colorOf(k) }))
  }
  const apiDim = dimApiName[dim.value]
  const perPoint = points.map(p => p.dims?.[apiDim] ?? {})
  const sums = new Map<string, number>()
  for (const m of perPoint)
    for (const [k, v] of Object.entries(m)) sums.set(k, (sums.get(k) ?? 0) + v)
  const top = [...sums.entries()].sort((a, b) => b[1] - a[1]).slice(0, maxDimSeries).map(e => e[0])
  const restKeys = [...sums.keys()].filter(k => !top.includes(k))
  const shortName = (k: string) => (k.length > 28 ? `${k.slice(0, 28)}…` : k)
  const series = top.map(k => ({
    name: shortName(k),
    data: perPoint.map(m => m[k] ?? 0),
    color: undefined as string | undefined,
  }))
  if (restKeys.length > 0) {
    series.push({
      name: `其他（${restKeys.length}）`,
      data: perPoint.map(m => restKeys.reduce((s, k) => s + (m[k] ?? 0), 0)),
      color: undefined as string | undefined,
    })
  }
  return series
}

let lastPoints: AasPoint[] = []

/** AAS 主图 option（数据未到位返回 null 跳过渲染） */
function mainOption() {
  const points = lastPoints
  const series = buildSeries(points)
  const times = points.map(p =>
    dayjs(p.timeUtc).format(isRealtime() ? 'HH:mm:ss' : 'MM-DD HH:mm'))

  // Waits 维度：图例 = 桶 key（英文），tooltip / 环图追加中文名
  const zhMap: Record<string, string> = {}
  if (dim.value === 'wait')
    for (const k of aasBucketOrder)
      zhMap[displayKey(k)] = k === 'io' ? 'IO（日志写+系统 IO）' : bucketNames.value[k]
  const zhOf = dim.value === 'wait' ? (n: string) => zhMap[n] : undefined

  return {
    tooltip: {
      trigger: 'axis',
      confine: true,
      ...(zhOf
        ? {
            formatter: (params: any) => {
              if (!Array.isArray(params) || !params.length) return ''
              const lines = params.map((p: any) => {
                const zh = zhOf(p.seriesName)
                return `${p.marker}${zh ? `${p.seriesName}（${zh}）` : p.seriesName}　${p.value}`
              })
              return `${params[0].axisValue}<br/>${lines.join('<br/>')}`
            },
          }
        : {}),
    },
    legend: { type: 'scroll', bottom: 0 },
    grid: { left: 48, right: 24, top: 32, bottom: 56 },
    xAxis: { type: 'category', data: times, boundaryGap: false },
    yAxis: {
      type: 'value',
      name: '活跃会话数',
      minInterval: 1,
      splitLine: { lineStyle: { type: 'dashed' } },   // 虚线水平网格（色值走主题）
    },
    series: series.map((s, i) => ({
      name: s.name,
      type: 'line' as const,
      stack: 'aas',
      lineStyle: { color: '#fff', width: 1 },   // 白边：避免相邻色层混叠成脏色
      ...(s.color
        ? { itemStyle: { color: s.color }, areaStyle: { color: s.color, opacity: 0.85 } }
        : { areaStyle: { opacity: 0.85 } }),
      emphasis: { focus: 'series' as const },
      symbol: 'none',
      smooth: false,
      data: s.data,
      // max vCores 参考线只挂第一个系列（全局生效）
      ...(i === 0 && cpuCores.value
        ? {
            markLine: {
              silent: true,
              symbol: 'none',
              lineStyle: { color: CHART_COLORS.danger, type: 'dashed' as const, width: 1.5 },
              label: { formatter: `max vCores = ${cpuCores.value}`, position: 'insideEndTop' as const },
              data: [{ yAxis: cpuCores.value }],
            },
          }
        : {}),
    })),
  }
}

/** 图表生命周期统一（主图 key='' / 详情趋势 key='trend'）：懒渲染 + RO 自动 resize + keep-alive 兜底 */
const { bind, render: renderChart } = useCharts(key => (key === 'trend' ? trendOption() : mainOption()))

function render(points: AasPoint[]) {
  lastPoints = points
  renderChart()
}

// ---------- SQL 详情弹窗（全文 + AAS 趋势） ----------
const detailOpen = ref(false)
const detailLoading = ref(false)
const detailSql = ref('')
const detailFp = ref('')
const trendPoints = ref<{ timeUtc: string; value?: number | null }[]>([])

/** 详情趋势 option（数据未到位返回 null 跳过渲染） */
function trendOption() {
  const points = trendPoints.value
  if (!points.length) return null
  return {
    tooltip: { trigger: 'axis', confine: true },
    grid: { left: 48, right: 24, top: 16, bottom: 28 },
    xAxis: {
      type: 'category',
      data: points.map(p => dayjs(p.timeUtc).format(isRealtime() ? 'HH:mm:ss' : 'MM-DD HH:mm')),
      boundaryGap: false,
    },
    yAxis: { type: 'value', name: 'AAS', minInterval: 1 },
    series: [{
      type: 'line' as const,
      symbol: 'none',
      step: false,
      connectNulls: false,
      areaStyle: { opacity: 0.25 },
      lineStyle: { color: CHART_COLORS.primary },
      itemStyle: { color: CHART_COLORS.primary },
      data: points.map(p => p.value ?? null),
    }],
  }
}

async function openDetail(row: TopSqlItem) {
  detailFp.value = row.fingerprint
  detailSql.value = row.sqlText ?? ''
  detailOpen.value = true
  detailLoading.value = true
  trendPoints.value = []
  try {
    const { start, end } = currentRange()
    if (!start || !end) return
    const r = await getSqlTrend(instanceId.value!, row.fingerprint, start, end)
    if (r.sqlText) detailSql.value = r.sqlText
    trendPoints.value = r.points
    await nextTick()
    renderChart('trend')
  } catch {
    // apiGet 已提示
  } finally {
    detailLoading.value = false
  }
}

function closeDetail() {
  detailOpen.value = false
}

// ---------- 联动 ----------
async function onRangeChange() {
  stopPolling()
  await fetch()
  syncPolling()
}

function syncPolling() {
  if (isRealtime() && autoRefresh.value) startPolling()
  else stopPolling()
}

watch(autoRefresh, syncPolling)
watch(dim, () => { /* 维度切换只重绘，不重新请求（数据已在手） */
  if (lastPoints.length) render(lastPoints)
})

// 图表 resize/dispose/keep-alive 兜底全部由 useCharts 托管（RO + onActivated）

onMounted(() => {
  init()
})

onBeforeUnmount(() => {
  stopPolling()
})

const topSqlColumns = [
  { title: '#', key: 'idx', width: 48 },
  { title: 'AAS', key: 'aas', width: 130 },
  { title: 'SQL 指纹', key: 'fingerprint', width: 200, ellipsis: true },
  { title: 'SQL 语句', key: 'sqlText', ellipsis: true },
]

const fmtAas = (v: number) => v.toFixed(3)
</script>

<template>
  <div class="page">
    <a-card>
      <!-- 实例在顶栏全局选择，页内不再重复 -->
      <div class="toolbar" style="margin-bottom: 12px">
        <span>时间：</span>
        <a-select :value="rangeKey" style="width: 140px" :options="rangeOptions" @change="onPresetChange" />
        <a-range-picker
          v-model:value="customRange"
          show-time
          format="YYYY-MM-DD HH:mm"
          :placeholder="['开始时间', '结束时间']"
          :disabled-date="disabledDate"
          @change="onCustomChange"
        />
        <a-button v-if="!isRealtime()" size="small" @click="fetch">刷新</a-button>
        <template v-if="isRealtime()">
          <span>自动刷新：</span>
          <a-switch v-model:checked="autoRefresh" size="small" />
          <span v-if="lastTickUtc" class="text-tertiary">最近采样 {{ dayjs(lastTickUtc).format('HH:mm:ss') }}</span>
        </template>
        <span class="text-tertiary">时间粒度：{{ granularity() }}</span>
      </div>

      <!-- 标题 + AAS 分类（同行，分类靠右） -->
      <div style="display: flex; align-items: center; margin-bottom: 8px">
        <span style="font-weight: 600">Average Active Sessions (AAS)</span>
        <div style="margin-left: auto; display: flex; align-items: center; gap: 8px">
          <span>AAS 分类：</span>
          <a-select
            v-model:value="dim"
            style="width: 180px"
            :options="(Object.entries(dimLabel) as [DimKey, string][]).map(([k, label]) => ({ value: k, label }))"
          />
        </div>
      </div>

      <!-- AAS 堆叠面积图：按所选维度分色 + max vCores 参考线（超过参考线即过载） -->
      <div v-show="!emptyReason" :ref="bind()" style="height: 360px"></div>
      <a-empty v-if="emptyReason" :description="emptyReason" style="padding: 80px 0" />
    </a-card>

    <!-- Load By SQL：区间 Top10 AAS 贡献 -->
    <a-card title="Load By SQL（点击行查看 SQL 详情与 AAS 趋势）">
      <a-table
        :columns="topSqlColumns"
        :data-source="topSql"
        :loading="topSqlLoading"
        :pagination="false"
        size="small"
        row-key="fingerprint"
        :custom-row="(row: TopSqlItem) => ({ onClick: () => openDetail(row), style: { cursor: 'pointer' } })"
      >
        <template #bodyCell="{ column, index, record }">
          <template v-if="column.key === 'idx'">{{ index + 1 }}</template>
          <template v-else-if="column.key === 'aas'">
            <div style="white-space: nowrap">
              {{ fmtAas((record as TopSqlItem).aas) }} <span class="text-tertiary">|</span>
              <span class="text-tertiary">{{ (record as TopSqlItem).percent }}%</span>
            </div>
            <!-- AAS 构成条（第二行固定）：总长按该行 AAS/榜首 AAS 缩放，分段按构成占比、颜色与趋势图同源 -->
            <div
              v-if="sqlBreakdown(record as TopSqlItem).length"
              :style="{
                display: 'flex',
                width: `${barWidth(record as TopSqlItem)}px`,
                height: '8px',
                marginTop: '3px',
                borderRadius: '2px',
                overflow: 'hidden',
              }"
            >
              <span
                v-for="[k, v] in sqlBreakdown(record as TopSqlItem)"
                :key="k"
                :title="`${displayKey(k)}（${bucketZh(k)}）: ${v.toFixed(2)}`"
                :style="{
                  width: `${(v / sqlBreakdownTotal(record as TopSqlItem)) * 100}%`,
                  background: colorOf(k),
                }"
              ></span>
            </div>
          </template>
          <template v-else-if="column.key === 'fingerprint'">
            <code style="font-size: 12px">{{ (record as TopSqlItem).fingerprint }}</code>
          </template>
          <template v-else-if="column.key === 'sqlText'">
            <span v-if="(record as TopSqlItem).sqlText" style="font-family: monospace; font-size: 12px">
              {{ (record as TopSqlItem).sqlText!.replace(/\s+/g, ' ').slice(0, 160) }}
            </span>
            <span v-else class="text-tertiary">（文本未采集）</span>
          </template>
        </template>
      </a-table>
    </a-card>

    <!-- SQL 详情弹窗 -->
    <a-modal
      :open="detailOpen"
      :title="`SQL 详情（${detailFp.slice(0, 16)}…）`"
      :width="880"
      :footer="null"
      @cancel="closeDetail"
    >
      <a-spin :spinning="detailLoading">
        <SqlText :text="detailSql || '（文本未采集）'" :max-height="200" />
        <div :ref="bind('trend')" style="height: 220px"></div>
      </a-spin>
    </a-modal>
  </div>
</template>
