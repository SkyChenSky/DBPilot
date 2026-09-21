<script setup lang="ts">
import { computed, onActivated, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { message } from 'ant-design-vue'
import dayjs from 'dayjs'
import type { ECharts } from 'echarts/core'
import {
  getUsageSnapshot,
  getUsageTrend,
  recollectUsage,
  type IndexUsageSnapshotItem,
  type IndexUsageSnapshotResult,
  type IndexUsageTrendPoint,
} from '../../api/indexDiag'
import EllipsisText from '../../components/EllipsisText.vue'
import StatCard from '../../components/StatCard.vue'
import { CHART_COLORS, initChart, rebindChart } from '../../charts/echarts'
import { useInstanceDatabases } from '../../composables/useInstanceDatabases'
import { fitColumnWidth, MINUTE_COL_W } from '../../utils/fitColumnWidth'
import { fmtTimeMinute } from '../../utils/format'
import { CAP, useEngineCaps } from '../../api/engine'

// 实例来自顶栏全局上下文：首载/切换 → 重拉库列表 + 重查快照
const { instanceId, dbOptions, dbName, dbLoading, init } = useInstanceDatabases(() => loadSnapshot())

// 引擎守卫（能力矩阵）：碎片扫描仅支持有碎片数据源的引擎——隐藏碎片交互入口与空数据构件（读写计数照常展示）
const { cap } = useEngineCaps()
const noFrag = computed(() => cap(CAP.fragmentation) === 'none')

// 切到无碎片数据的引擎：筛选若停留在「需碎片处理」重置回全部
watch(noFrag, (v) => {
  if (v && filter.value === 'frag') filter.value = 'all'
})

const loading = ref(false)
const excludeSystemDb = ref(false)
const snapshotResult = ref<IndexUsageSnapshotResult>()
const trendPoints = ref<IndexUsageTrendPoint[]>([])
const filter = ref<'all' | 'unused' | 'frag'>('all')
const page = ref(1)

const overview = computed(() => snapshotResult.value?.overview)
const items = computed<IndexUsageSnapshotItem[]>(() => snapshotResult.value?.items ?? [])

const totalCount = computed(() => items.value.length)
const unusedCount = computed(() => items.value.filter(i => i.isUnused).length)
const fragNeedCount = computed(() => items.value.filter(i => i.action !== 0).length)

const showFilters = ref(false)

const rows = computed(() => {
  let all = items.value
  switch (filter.value) {
    case 'unused': all = all.filter(i => i.isUnused); break
    case 'frag': all = all.filter(i => i.action !== 0); break
  }
  // 默认碎片率倒序（无碎片数据的行排最后）
  return [...all].sort((a, b) => (b.avgFragmentationPercent ?? -1) - (a.avgFragmentationPercent ?? -1)).filter(i => {
    if (conditions.tableName && !`${i.dbName}.${i.tableName}`.toLowerCase().includes(conditions.tableName.toLowerCase())) return false
    if (conditions.indexName && !i.indexName.toLowerCase().includes(conditions.indexName.toLowerCase())) return false
    if (conditions.frag != null && (i.avgFragmentationPercent ?? -1) < conditions.frag) return false
    if (conditions.sizeMb != null && sizeMb(i) < conditions.sizeMb) return false
    if (conditions.pages != null && (i.usedPageCount ?? -1) < conditions.pages) return false
    if (conditions.seeks != null && i.userSeeks < conditions.seeks) return false
    if (conditions.scans != null && i.userScans < conditions.scans) return false
    if (conditions.lookups != null && i.userLookups < conditions.lookups) return false
    return true
  })
})

// 阿里云同款筛选条件（null/空 = 不过滤；碎片率/页数 null 行在设置阈值时排除）
const conditions = reactive({
  tableName: '',
  indexName: '',
  frag: null as number | null,
  sizeMb: null as number | null,
  pages: null as number | null,
  seeks: null as number | null,
  scans: null as number | null,
  lookups: null as number | null,
})

/** 生效中的过滤条件数（折叠面板头提示） */
const activeFilterCount = computed(() => {
  const c = conditions
  let n = 0
  if (c.tableName.trim()) n++
  if (c.indexName.trim()) n++
  for (const v of [c.frag, c.sizeMb, c.pages, c.seeks, c.scans, c.lookups])
    if (v != null) n++
  return n
})

// ---------- 四张图卡（对齐阿里云索引使用率页） ----------

/** 碎片率分布：<10% / 10~30% / >30% / 无数据（<256 页不扫） */
const fragData = computed(() => {
  const b = [0, 0, 0, 0]
  for (const i of items.value) {
    const v = i.avgFragmentationPercent
    if (v == null) b[3]++
    else if (v < 10) b[0]++
    else if (v <= 30) b[1]++
    else b[2]++
  }
  return b
})

function reads(i: IndexUsageSnapshotItem) {
  return i.userSeeks + i.userScans + i.userLookups
}

/** 使用率 Top10（按读次数，Seek/Scan/Lookup 堆叠） */
const usageTop = computed(() =>
  [...items.value].sort((a, b) => reads(b) - reads(a)).slice(0, 10))

/** Top10 碎片率（降序，仅有碎片数据的行） */
const fragTop = computed(() =>
  items.value
    .filter(i => i.avgFragmentationPercent != null)
    .sort((a, b) => (b.avgFragmentationPercent ?? 0) - (a.avgFragmentationPercent ?? 0))
    .slice(0, 10))

/** "[dbo].[Orders].IX_x" → "Orders.IX_x"，超长截断（图卡宽度有限，10 字符级） */
function shortLabel(i: IndexUsageSnapshotItem) {
  const table = i.tableName.split(']').pop()?.replace(/^[.\[]+/, '') ?? i.tableName
  const label = `${table}.${i.indexName}`
  return label.length > 12 ? `${label.slice(0, 11)}…` : label
}

const fragDistEl = ref<HTMLDivElement>()
const usageTopEl = ref<HTMLDivElement>()
const fragTopEl = ref<HTMLDivElement>()
const spaceTrendEl = ref<HTMLDivElement>()
const charts: Record<string, ECharts | null> = {}

interface BarSeries {
  name?: string
  data: number[]
  color: string
  stack?: string
}

function setBar(
  key: string,
  el: HTMLDivElement | undefined,
  categories: string[],
  series: BarSeries[],
  opts: { legend?: boolean; rotate?: number; valueSuffix?: string } = {},
) {
  if (!el) return
  // 图卡行有 v-if，数据瞬时为空会卸载重建 div —— rebindChart 保证句柄绑的是当前 DOM
  charts[key] = rebindChart(charts[key], el) ?? initChart(el)
  charts[key]!.setOption({
    // containLabel：x 轴旋转标签计入网格布局，防止溢出画布
    grid: { left: 8, right: 8, top: opts.legend ? 28 : 12, bottom: 6, containLabel: true },
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: 'shadow' },
      valueFormatter: opts.valueSuffix ? (v: number) => `${v}${opts.valueSuffix}` : undefined,
    },
    legend: opts.legend ? { top: 0, itemWidth: 14, textStyle: { fontSize: 11 } } : undefined,
    xAxis: {
      type: 'category',
      data: categories,
      axisTick: { alignWithLabel: true },
      axisLabel: { fontSize: 9, rotate: opts.rotate ?? 0, interval: 0, hideOverlap: true },
    },
    yAxis: {
      type: 'value',
      minInterval: 1,
      axisLabel: { fontSize: 11 },
    },
    series: series.map(s => ({
      type: 'bar',
      name: s.name,
      data: s.data,
      stack: s.stack,
      barMaxWidth: 28,
      itemStyle: { color: s.color },
    })),
  }, true)
}

function renderCharts() {
  setBar('fragDist', fragDistEl.value, ['<10%', '10~30%', '>30%', '无数据'],
    [{ data: fragData.value, color: CHART_COLORS.primary }])

  setBar('usageTop', usageTopEl.value, usageTop.value.map(shortLabel), [
    { name: 'Seek', data: usageTop.value.map(i => i.userSeeks), color: CHART_COLORS.primary, stack: 'total' },
    { name: 'Scan', data: usageTop.value.map(i => i.userScans), color: CHART_COLORS.success, stack: 'total' },
    { name: 'Lookup', data: usageTop.value.map(i => i.userLookups), color: CHART_COLORS.warning, stack: 'total' },
  ], { legend: true, rotate: 30 })

  setBar('fragTop', fragTopEl.value, fragTop.value.map(shortLabel),
    [{ data: fragTop.value.map(i => Number((i.avgFragmentationPercent ?? 0).toFixed(1))), color: CHART_COLORS.danger }],
    { rotate: 30, valueSuffix: '%' })

  // 空间变化趋势：同日多批次（凌晨 Job + 手动补采）时标签带时刻，否则只显示日期
  const days = new Set(trendPoints.value.map(p => dayjs(p.snapshotTimeUtc).format('MM-DD')))
  const fmt = days.size < trendPoints.value.length ? 'MM-DD HH:mm' : 'MM-DD'
  setBar('spaceTrend', spaceTrendEl.value,
    trendPoints.value.map(p => dayjs(p.snapshotTimeUtc).format(fmt)),
    [{ data: trendPoints.value.map(p => Math.round(p.totalPages * 8 / 1024)), color: CHART_COLORS.primary }],
    { valueSuffix: ' MB' })
}

watch([trendPoints, fragData, usageTop, fragTop], renderCharts, { flush: 'post' })

function onResize() {
  for (const c of Object.values(charts)) c && !c.isDisposed() && c.resize()
}

onBeforeUnmount(() => {
  window.removeEventListener('resize', onResize)
  for (const c of Object.values(charts)) c?.dispose()
})

// ---------- 表格数值条形（大小/碎片率，对齐阿里云） ----------

function sizeMb(i: IndexUsageSnapshotItem) {
  return Math.round(((i.usedPageCount ?? 0) * 8 / 1024) * 10) / 10
}

const maxSizeMb = computed(() => Math.max(1, ...rows.value.map(sizeMb)))

function barWidth(v: number, max: number) {
  return `${Math.min(100, Math.round((v / max) * 100))}%`
}

/** 碎片率条形颜色：红 >30 / 橙 ≥10 / 蓝 */
function fragBarStyle(v: number) {
  const color = v > 30 ? CHART_COLORS.danger : v >= 10 ? CHART_COLORS.warning : CHART_COLORS.primary
  return {
    background: `${color}1a`,
    borderRight: `2px solid ${color}73`,
  }
}

function fragTextStyle(v: number) {
  return v > 30 ? 'color: var(--danger-text); font-weight: 600' : v >= 10 ? 'color: var(--warning-text)' : ''
}

// ---------- 数据加载 / 重新采集 ----------

async function loadSnapshot() {
  if (instanceId.value === undefined) return
  loading.value = true
  try {
    snapshotResult.value = await getUsageSnapshot(instanceId.value, dbName.value ?? '', excludeSystemDb.value)
    trendPoints.value = await getUsageTrend(instanceId.value, dbName.value ?? '', excludeSystemDb.value)
  } finally {
    loading.value = false
  }
}

/** 立即重新采集（与每日 Job 同一代码路径，含碎片扫描；大库分钟级，超时已放宽 10 分钟） */
async function recollect() {
  if (instanceId.value === undefined) return
  loading.value = true
  try {
    await recollectUsage(instanceId.value)
    await loadSnapshot()
    message.success('重新采集完成')
  } catch {
    // 冷却拒绝/采集失败：拦截器已弹错误提示
  } finally {
    loading.value = false
  }
}

// ---------- 脚本导出 / 复制 ----------

async function copySql(sql: string) {
  try {
    await navigator.clipboard.writeText(sql)
    message.success('脚本已复制')
  } catch {
    message.error('复制失败，请手动选择复制')
  }
}

/** 导出口径 = 当前列表过滤后的行（rows），与页面所见一致 */
const exportableUnused = computed(() => rows.value.filter(i => i.isUnused && i.disableScript))
const exportableFrag = computed(() => rows.value.filter(i => i.action !== 0 && i.fragScript))

/** 导出未使用索引的 DISABLE 脚本（按库分组 USE，保守方案 + 观察期说明） */
function exportUnused() {
  const unused = exportableUnused.value
  if (!unused.length) return
  const sections = Object.entries(
    unused.reduce<Record<string, typeof unused>>((acc, i) => {
      ;(acc[i.dbName] ??= []).push(i)
      return acc
    }, {}),
  ).map(([db, list]) => `USE [${db}];\nGO\n${list
    .map(i => `-- ${i.tableName}.${i.indexName} | 读 0 / 写 ${i.userUpdates} / ${i.usedPageCount ?? 0} 页\n${i.disableScript}`)
    .join('\n')}`)
  const blob = new Blob(
    [`${sections.join('\n\n')}\n\n-- 保守方案：DISABLE 不删除数据，观察期（建议 ≥ 1 周）无异常后再评估删除\n`],
    { type: 'text/plain;charset=utf-8' },
  )
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = `unused_indexes_${dbName.value || 'all'}.sql`
  a.click()
  URL.revokeObjectURL(a.href)
}

/** 导出碎片处理脚本（REBUILD / REORGANIZE 分组；与列表"需碎片处理"同口径，
 *  页数 <1000 的小表行脚本以注释保留——数量与列表一致，需要时可自行放开） */
function exportFrag() {
  const need = exportableFrag.value
  if (!need.length) {
    message.info('没有需要处理的索引')
    return
  }
  const sections = [
    ['REBUILD（碎片率 > 30%）', need.filter(i => i.action === 2)],
    ['REORGANIZE（碎片率 10% ~ 30%）', need.filter(i => i.action === 1)],
  ]
    .filter(([, list]) => (list as IndexUsageSnapshotItem[]).length)
    .map(([title, list]) =>
      `-- ${title}\n${(list as IndexUsageSnapshotItem[]).map((i) => {
        const head = `-- ${i.tableName}.${i.indexName} | 碎片 ${(i.avgFragmentationPercent ?? 0).toFixed(1)}% / ${i.usedPageCount ?? 0} 页${i.skipSmall ? ' | 小表：不建议处理，脚本已注释' : ''}`
        const script = i.skipSmall
          ? i.fragScript!.split('\n').map(l => `-- ${l}`).join('\n')
          : i.fragScript!
        return `${head}\n${script}`
      }).join('\n')}`)
  const useClause = dbName.value ? `USE [${dbName.value}];\nGO\n\n` : ''
  const blob = new Blob([`${useClause}${sections.join('\n\n')}\n\n-- 建议业务低峰期执行；大表 REBUILD 优先 ONLINE（脚本已按实例版本自动处理）\n`], { type: 'text/plain;charset=utf-8' })
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = `frag_scripts_${dbName.value || 'all'}.sql`
  a.click()
  URL.revokeObjectURL(a.href)
}

/** 读写总量（占比分母，阿里云口径：查找+扫描+书签查找+更新） */
function totalOps(r: IndexUsageSnapshotItem) {
  return r.userSeeks + r.userScans + r.userLookups + r.userUpdates
}

/** 各读写分量占总读写比（62.59% 样式；总量 0 无占比） */
function ratio(v: number, total: number) {
  return total > 0 ? `${Math.round((v / total) * 10000) / 100}%` : ''
}

function actionText(a: 0 | 1 | 2) {
  return a === 2 ? 'REBUILD' : a === 1 ? 'REORGANIZE' : '无需处理'
}

function actionColor(a: 0 | 1 | 2) {
  return a === 2 ? 'red' : a === 1 ? 'orange' : 'green'
}

/** 维护操作的理由（阿里云口径） */
function reasonText(r: IndexUsageSnapshotItem) {
  if (r.action === 2) return '碎片率>30%'
  if (r.action === 1) return '碎片率10%~30%'
  if (r.isUnused) return '读=0 且写放大'
  return '-'
}

/** 处理优先级：REBUILD 高 / REORGANIZE 中 / 未使用 低 */
function priorityText(r: IndexUsageSnapshotItem) {
  return r.action === 2 ? '高' : r.action === 1 ? '中' : r.isUnused ? '低' : '-'
}

function priorityColor(r: IndexUsageSnapshotItem) {
  return r.action === 2 ? 'red' : r.action === 1 ? 'orange' : r.isUnused ? 'default' : ''
}

// 名称类列按数据最长值量宽（带上限，超出点击看全）
const tableColW = computed(() => fitColumnWidth(items.value.map(i => `${i.dbName}.${i.tableName}`), { min: 140, max: 240 }))
const indexColW = computed(() => fitColumnWidth(items.value.map(i => i.indexName), { min: 130, max: 200 }))
const colsColW = computed(() => fitColumnWidth(items.value.map(i => i.keyColumns), { min: 110, max: 220 }))

// keep-alive 切回：容器重新挂载，按当前尺寸重排（缓存期间窗口/侧栏变化的兜底）
onActivated(() => onResize())

onMounted(async () => {
  window.addEventListener('resize', onResize)
  // 打开页面默认查询全部：全局实例 + "全部库"
  await init()
})
</script>

<template>
  <div class="page">
    <a-card>
      <!-- 工具栏：库 + 重新采集 + 导出（实例在顶栏全局选择） -->
      <div class="toolbar" style="margin-bottom: 16px">
        <span>数据库：</span>
        <a-select
          v-model:value="dbName"
          style="width: 200px"
          placeholder="选择数据库"
          :loading="dbLoading"
          :options="dbOptions"
          @change="loadSnapshot"
        />
        <a-tooltip title="全部库视图下额外排除 master/model/msdb/tempdb 的索引（选具体库时无效）">
          <a-switch
            v-model:checked="excludeSystemDb"
            checked-children="排除系统库"
            un-checked-children="含系统库"
            :disabled="!!dbName"
            @change="loadSnapshot"
          />
        </a-tooltip>
        <a-button :loading="loading" :disabled="instanceId === undefined" @click="loadSnapshot">刷新</a-button>
        <a-button type="primary" :loading="loading" :disabled="instanceId === undefined" @click="recollect">重新采集</a-button>
        <a-button :disabled="!exportableUnused.length" @click="exportUnused">导出未使用脚本</a-button>
        <a-button v-if="!noFrag" :disabled="!exportableFrag.length" @click="exportFrag">导出碎片脚本</a-button>
      </div>

      <!-- 总览统计（对齐阿里云；附注走 hint 提示；MySQL 无碎片卡降为三列） -->
      <div v-if="overview" class="overview-grid" :style="noFrag ? 'grid-template-columns: repeat(3, minmax(0, 1fr))' : undefined">
        <StatCard
          title="索引总量"
          :value="overview.total"
          unit="条"
          :hint="`总空间 ${overview.totalSpaceMb.toFixed(1)} MB`"
        />
        <StatCard
          title="总页数"
          :value="overview.totalPages"
          :hint="snapshotResult?.snapshotTimeUtc ? `数据更新于 ${fmtTimeMinute(snapshotResult.snapshotTimeUtc)}` : '暂无快照（每日采集）'"
        />
        <StatCard v-if="!noFrag" title="碎片率>30%" :value="overview.fragOver30Count" unit="条" />        <StatCard
          title="查找<100"
          :value="`${overview.lowReadCount} 条`"
          :hint="`读占比<10%：${overview.lowReadRatioCount} 条`"
        />
      </div>

      <!-- 四张图卡（对齐阿里云；MySQL 无碎片双卡，余两卡各占半行） -->
      <a-row v-if="overview" :gutter="12" style="margin-bottom: 16px">
        <a-col v-if="!noFrag" :span="6">
          <a-card size="small" title="碎片率分布">
            <div ref="fragDistEl" style="height: 160px"></div>
          </a-card>
        </a-col>
        <a-col :span="noFrag ? 12 : 6">
          <a-card size="small" title="使用率 Top10（读次数）">
            <div ref="usageTopEl" style="height: 160px"></div>
          </a-card>
        </a-col>
        <a-col v-if="!noFrag" :span="6">
          <a-card size="small" title="Top10 碎片率">
            <div ref="fragTopEl" style="height: 160px"></div>
          </a-card>
        </a-col>
        <a-col :span="noFrag ? 12 : 6">
          <a-card size="small" title="空间变化趋势">
            <div ref="spaceTrendEl" style="height: 160px"></div>
          </a-card>
        </a-col>
      </a-row>

      <a-alert
        v-if="snapshotResult && !snapshotResult.snapshotTimeUtc"
        type="info"
        show-icon
        style="margin-bottom: 16px"
        message="暂无快照数据：每日自动采集，或点击「重新采集」立即执行"
      />

      <!-- 筛选 -->
      <div class="toolbar" style="margin-bottom: 12px">
        <a-radio-group v-model:value="filter" button-style="solid">
          <a-radio-button value="all">全部（{{ totalCount }}）</a-radio-button>
          <a-radio-button value="unused">未使用（{{ unusedCount }}）</a-radio-button>
          <a-radio-button v-if="!noFrag" value="frag">需碎片处理（{{ fragNeedCount }}）</a-radio-button>
        </a-radio-group>
      </div>

      <!-- 过滤条件（对齐阿里云；空 = 不过滤；折叠面板收纳） -->
      <a-collapse :active-key="showFilters ? ['filters'] : []" ghost @change="(keys: any) => showFilters = keys.length > 0">
        <a-collapse-panel key="filters" header="过滤条件">
          <template #header>
            <span>过滤条件</span>
            <a-tag v-if="activeFilterCount" color="blue" style="margin-left: 8px">{{ activeFilterCount }} 项生效</a-tag>
          </template>
          <div class="toolbar">
            <span class="text-tertiary">表名称：</span>
            <a-input v-model:value="conditions.tableName" placeholder="模糊匹配" allowClear style="width: 130px" />
            <span class="text-tertiary">索引名称：</span>
            <a-input v-model:value="conditions.indexName" placeholder="模糊匹配" allowClear style="width: 130px" />
            <template v-if="!noFrag">
              <span class="text-tertiary">碎片率 ≥</span>
              <a-input-number v-model:value="conditions.frag" :min="0" allowClear placeholder="不限" style="width: 80px" />
              <span>%</span>
            </template>
            <span class="text-tertiary">大小 ≥</span>
            <a-input-number v-model:value="conditions.sizeMb" :min="0" allowClear placeholder="不限" style="width: 90px" />
            <span>MB</span>
            <span class="text-tertiary">页数 ≥</span>
            <a-input-number v-model:value="conditions.pages" :min="0" allowClear placeholder="不限" style="width: 80px" />
            <span>页</span>
            <span class="text-tertiary">查找 ≥</span>
            <a-input-number v-model:value="conditions.seeks" :min="0" allowClear placeholder="不限" style="width: 80px" />
            <span>次</span>
            <span class="text-tertiary">扫描 ≥</span>
            <a-input-number v-model:value="conditions.scans" :min="0" allowClear placeholder="不限" style="width: 80px" />
            <span>次</span>
            <span class="text-tertiary">书签查找 ≥</span>
            <a-input-number v-model:value="conditions.lookups" :min="0" allowClear placeholder="不限" style="width: 80px" />
            <span>次</span>
          </div>
        </a-collapse-panel>
      </a-collapse>

      <a-table
        :data-source="rows"
        :loading="loading"
        :row-key="(r: any) => `${r.dbName}.${r.tableName}.${r.indexName}`"
        :pagination="{ current: page, pageSize: 20, showTotal: (t: number) => `共 ${t} 条` }"
        :scroll="{ x: 1060 + tableColW + indexColW + colsColW + MINUTE_COL_W }"
        size="small"
        @change="(p: any) => page = p.current"
      >
        <a-table-column title="序号" :width="48" align="center">
          <template #default="{ index }">{{ (page - 1) * 20 + index + 1 }}</template>
        </a-table-column>
        <a-table-column title="表" :width="tableColW">
          <template #default="{ record }"><EllipsisText :text="`${record.dbName}.${record.tableName}`" viewer-title="表" mono /></template>
        </a-table-column>
        <a-table-column title="索引" :width="indexColW">
          <template #default="{ record }">
            <div class="oneline" :title="[record.indexName, record.isUnique ? 'UQ' : '', record.isUnused ? '未使用' : ''].filter(Boolean).join(' ｜ ')">
              <span>{{ record.indexName }}</span>
              <a-tag v-if="record.isUnique" color="purple" style="margin-left: 4px">UQ</a-tag>
              <a-tag v-if="record.isUnused" color="red" style="margin-left: 4px">未使用</a-tag>
            </div>
          </template>
        </a-table-column>
        <a-table-column title="处理碎片率" :width="90" :sorter="(a: any, b: any) => (a.avgFragmentationPercent ?? -1) - (b.avgFragmentationPercent ?? -1)">
          <template #default="{ record }">
            <div v-if="record.avgFragmentationPercent != null" class="numcell">
              <div class="numbar" :style="{ width: `${Math.min(100, Math.round(record.avgFragmentationPercent))}%`, ...fragBarStyle(record.avgFragmentationPercent) }"></div>
              <span :style="fragTextStyle(record.avgFragmentationPercent)">{{ record.avgFragmentationPercent.toFixed(1) }}%</span>
            </div>
            <span v-else class="text-disabled">-</span>
          </template>
        </a-table-column>
        <a-table-column title="大小(MB)" :width="85" :sorter="(a: any, b: any) => (a.usedPageCount ?? 0) - (b.usedPageCount ?? 0)">
          <template #default="{ record }">
            <div class="numcell">
              <div class="numbar" :style="{ width: barWidth(sizeMb(record), maxSizeMb) }"></div>
              <span>{{ sizeMb(record) }}</span>
            </div>
          </template>
        </a-table-column>
        <a-table-column title="维护操作" :width="110">
          <template #default="{ record }">
            <a-tooltip v-if="record.action !== 0 && record.skipSmall" title="页数 < 1000，小表不建议处理（收益低于维护成本）">
              <a-tag>{{ actionText(record.action) }} ·小表</a-tag>
            </a-tooltip>
            <a-tag v-else-if="record.action !== 0" :color="actionColor(record.action)">{{ actionText(record.action) }}</a-tag>
            <span v-else class="text-disabled">-</span>
          </template>
        </a-table-column>
        <a-table-column title="理由" :width="105">
          <template #default="{ record }">
            <span class="oneline" :class="reasonText(record) === '-' ? 'text-disabled' : ''" :title="reasonText(record) === '-' ? undefined : reasonText(record)">{{ reasonText(record) }}</span>
          </template>
        </a-table-column>
        <a-table-column title="优先级" :width="58" align="center">
          <template #default="{ record }">
            <a-tag v-if="priorityText(record) !== '-'" :color="priorityColor(record)">{{ priorityText(record) }}</a-tag>
            <span v-else class="text-disabled">-</span>
          </template>
        </a-table-column>
        <a-table-column title="页数" data-index="usedPageCount" :width="70" :sorter="(a: any, b: any) => (a.usedPageCount ?? 0) - (b.usedPageCount ?? 0)">
          <template #default="{ record }">{{ record.usedPageCount ?? '-' }}</template>
        </a-table-column>
        <a-table-column title="查找" data-index="userSeeks" :width="90" :sorter="(a: any, b: any) => a.userSeeks - b.userSeeks">
          <template #default="{ record }"><div class="oneline" :title="`${record.userSeeks}（占读写总量 ${ratio(record.userSeeks, totalOps(record)) || '0'}）`">{{ record.userSeeks }} <span class="ratiotxt">{{ ratio(record.userSeeks, totalOps(record)) }}</span></div></template>
        </a-table-column>
        <a-table-column title="扫描" data-index="userScans" :width="90" :sorter="(a: any, b: any) => a.userScans - b.userScans">
          <template #default="{ record }"><div class="oneline" :title="`${record.userScans}（占读写总量 ${ratio(record.userScans, totalOps(record)) || '0'}）`">{{ record.userScans }} <span class="ratiotxt">{{ ratio(record.userScans, totalOps(record)) }}</span></div></template>
        </a-table-column>
        <a-table-column title="书签查找" data-index="userLookups" :width="90" :sorter="(a: any, b: any) => a.userLookups - b.userLookups">
          <template #default="{ record }"><div class="oneline" :title="`${record.userLookups}（占读写总量 ${ratio(record.userLookups, totalOps(record)) || '0'}）`">{{ record.userLookups }} <span class="ratiotxt">{{ ratio(record.userLookups, totalOps(record)) }}</span></div></template>
        </a-table-column>
        <a-table-column title="更新" data-index="userUpdates" :width="90" :sorter="(a: any, b: any) => a.userUpdates - b.userUpdates">
          <template #default="{ record }"><div class="oneline" :title="`${record.userUpdates}（占读写总量 ${ratio(record.userUpdates, totalOps(record)) || '0'}）`">{{ record.userUpdates }} <span class="ratiotxt">{{ ratio(record.userUpdates, totalOps(record)) }}</span></div></template>
        </a-table-column>
        <a-table-column title="主键" :width="52" align="center">
          <template #default="{ record }">{{ record.isPrimaryKey ? '是' : '否' }}</template>
        </a-table-column>
        <a-table-column title="列" :width="colsColW">
          <template #default="{ record }"><EllipsisText :text="record.keyColumns" viewer-title="索引列" mono /></template>
        </a-table-column>
        <a-table-column title="最近读" :width="MINUTE_COL_W">
          <template #default="{ record }">{{ fmtTimeMinute(record.lastUserSeek ?? record.lastUserScan) }}</template>
        </a-table-column>
        <a-table-column title="操作" :width="82" fixed="right">
          <template #default="{ record }">
            <a-tooltip v-if="record.disableScript" :title="record.disableScript">
              <a @click="copySql(record.disableScript)">禁用脚本</a>
            </a-tooltip>
            <a-tooltip v-else-if="!noFrag && record.fragScript" :title="record.fragScript">
              <a @click="copySql(record.fragScript)">碎片脚本</a>
            </a-tooltip>
            <span v-else>-</span>
          </template>
        </a-table-column>
      </a-table>
    </a-card>
  </div>
</template>

<style scoped>
/* 总览统计四列网格 */
.overview-grid {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 12px;
  margin-bottom: 16px;
}

/* 数值条形（numcell/numbar）与单行省略（oneline）已收敛至 style.css 全局 */

/* 读写占比（阿里云"932 | 62.59%"样式） */
.ratiotxt {
  color: var(--text-tertiary);
  font-size: 12px;
}

/* 单行显示：不换行 + 超宽省略（表格行一行过） */
.oneline {
  display: block;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

/* 小密度表格：tag 收紧 */
:deep(.ant-table-small .ant-tag) {
  margin-inline-end: 0;
  padding: 0 5px;
  font-size: 12px;
  line-height: 18px;
}
</style>
