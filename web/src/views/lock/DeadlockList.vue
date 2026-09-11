<script setup lang="ts">
import { computed, onActivated, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import dayjs from 'dayjs'
import type { ECharts } from 'echarts/core'
import {
  getDeadlockDetail,
  getDeadlockFingerprints,
  getDeadlockFilters,
  getDeadlockMetricsTrend,
  getDeadlocks,
  getDeadlockStats,
  type DeadlockDetail,
  type DeadlockFingerprintStat,
  type DeadlockListItem,
  type DeadlockResource,
} from '../../api/deadlock'
import EllipsisText from '../../components/EllipsisText.vue'
import { CHART_COLORS, initChart, rebindChart } from '../../charts/echarts'
import { useGlobalInstance } from '../../composables/useGlobalInstance'
import { palette } from '../../theme/palette'
import { useTimeRange } from '../../composables/useTimeRange'
import { TIME_COL_W } from '../../utils/fitColumnWidth'
import { fmtTime } from '../../utils/format'
import { CAP, useEngineCaps } from '../../api/engine'

const router = useRouter()

// 实例来自顶栏全局上下文：首载/切换 → onInstanceChange（清指纹/登录/主机后重查）
const { instanceId, init } = useGlobalInstance(() => onInstanceChange())

// 引擎能力（能力矩阵驱动）：完整形态 = 事件明细 + 锁类型分色（deadlockEvents）；
// 无事件数据源但有计数器的引擎（MySQL/PostgreSQL）降级为死锁速率趋势（deadlockTrend，读指标序列聚合）；
// 两者皆无 → 整页空态（深链兜底；侧栏菜单先行隐藏）
const { cap, capsReady } = useEngineCaps()
const hasEvents = computed(() => cap(CAP.deadlockEvents) !== 'none')
const hasTrendOnly = computed(() => !hasEvents.value && cap(CAP.deadlockTrend) !== 'none')

const loading = ref(false)
const rows = ref<DeadlockListItem[]>([])
const total = ref(0)
const query = reactive({ page: 1, limit: 10 })

/** 时间档位：默认 24h（档位↔自定义联动统一走 useTimeRange） */
const { rangeKey, rangeOptions, customRange, onPresetChange, onCustomChange, fromTo, disabledDate } = useTimeRange({
  defaultKey: '24h',
  onApply: () => search(),
})

// ---------- 死锁趋势（锁资源类型分色堆叠；分色=事件×类型各计一次，总数=按事件计） ----------
const trendEl = ref<HTMLDivElement>()
let trendChart: ECharts | null = null
const trendTotal = ref(0)
const trendSpanMinutes = ref(1440)

// 锁资源类型分色：语义固定（主指标/成功/等待/紫/中性）
const TREND_SERIES = [
  { name: 'KEY 锁', key: 'keyLocks', color: CHART_COLORS.primary },
  { name: 'OBJECT 锁', key: 'objectLocks', color: CHART_COLORS.success },
  { name: 'PAGE 锁', key: 'pageLocks', color: CHART_COLORS.warning },
  { name: 'RID 锁', key: 'ridLocks', color: CHART_COLORS.purple },
  { name: '其他', key: 'otherLocks', color: palette.text.tertiary },
] as const

async function loadTrend() {
  if (instanceId.value === undefined || !trendEl.value) return
  const { from, to } = fromTo()
  const points = await getDeadlockStats(instanceId.value, { from, to })
  trendTotal.value = points.reduce((s, p) => s + p.total, 0)
  trendSpanMinutes.value = from && to ? dayjs(to).diff(dayjs(from), 'minute') : 1440

  const labelFmt = trendSpanMinutes.value > 48 * 60 ? 'MM-DD' : 'MM-DD HH:mm'
  trendChart = rebindChart(trendChart, trendEl.value) ?? initChart(trendEl.value)
  trendChart.setOption({
    grid: { left: 48, right: 24, top: 36, bottom: 28 },
    tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
    legend: {
      data: TREND_SERIES.map(s => s.name), top: 0, left: 0,
      itemWidth: 14, textStyle: { fontSize: 11 },
    },
    xAxis: {
      type: 'category',
      data: points.map(p => dayjs(p.timeUtc).format(labelFmt)),
      axisTick: { alignWithLabel: true },
      axisLabel: { fontSize: 11 },
    },
    yAxis: {
      type: 'value',
      minInterval: 1,
      axisLabel: { fontSize: 11 },
    },
    series: TREND_SERIES.map(s => ({
      name: s.name, type: 'bar', stack: 'locks',
      data: points.map(p => p[s.key]),
      barMaxWidth: 16, itemStyle: { color: s.color },
    })),
  }, true)
}

function onResize() {
  trendChart && !trendChart.isDisposed() && trendChart.resize()
  rateChart && !rateChart.isDisposed() && rateChart.resize()
}

// ---------- 降级形态：死锁速率趋势（指标序列按桶均值聚合，不伪造事件计数） ----------
const rateEl = ref<HTMLDivElement>()
let rateChart: ECharts | null = null
const rateAvg = ref<number | null>(null)

async function loadMetricsTrend() {
  if (instanceId.value === undefined || !rateEl.value) return
  const { from, to } = fromTo()
  const data = await getDeadlockMetricsTrend(instanceId.value, { from, to })
  const vals = data.deadlocksPerSec.filter((v): v is number => v != null)
  rateAvg.value = vals.length ? vals.reduce((s, v) => s + v, 0) / vals.length : null

  const spanMinutes = from && to ? dayjs(to).diff(dayjs(from), 'minute') : 1440
  const labelFmt = spanMinutes > 48 * 60 ? 'MM-DD' : 'MM-DD HH:mm'
  rateChart = rebindChart(rateChart, rateEl.value) ?? initChart(rateEl.value)
  rateChart.setOption({
    grid: { left: 48, right: 24, top: 20, bottom: 28 },
    tooltip: {
      trigger: 'axis',
      valueFormatter: (v: unknown) => (v == null ? '-' : `${Number(v).toFixed(4)} 次/秒`),
    },
    xAxis: {
      type: 'category',
      data: data.times.map(t => dayjs(t).format(labelFmt)),
      axisTick: { alignWithLabel: true },
      axisLabel: { fontSize: 11 },
    },
    yAxis: { type: 'value', axisLabel: { fontSize: 11 } },
    series: [{
      name: '死锁速率', type: 'line',
      data: data.deadlocksPerSec,
      connectNulls: false, showSymbol: false,
      lineStyle: { color: CHART_COLORS.primary },
      itemStyle: { color: CHART_COLORS.primary },
    }],
  }, true)
}

// ---------- 相似死锁归并 TOP（同指纹 = 同构死锁反复发生；点击行按指纹过滤列表） ----------
const fingerprints = ref<DeadlockFingerprintStat[]>([])

async function loadFingerprints() {
  if (instanceId.value === undefined) return
  const { from, to } = fromTo()
  const list = await getDeadlockFingerprints(instanceId.value, { from, to })
  // 只展示发生 >= 2 次的形态：单次偶发没有归并价值
  fingerprints.value = list.filter(f => f.count >= 2)
}

const fpColumns = [
  { title: '死锁形态（涉及对象）', key: 'objects' },
  { title: '牺牲方', key: 'victim' , width: 240 },
  { title: '发生次数', dataIndex: 'count', width: 90 },
  { title: '首次发生', key: 'first', width: TIME_COL_W },
  { title: '最近发生', key: 'last', width: TIME_COL_W },
]

// ---------- 列表过滤（锁类型/对象名/登录名/主机名 + 归并卡点击的指纹） ----------
const lockType = ref<string>()
const loginName = ref<string>()
const hostName = ref<string>()
const objectName = ref('')
/** 归并卡点击带入的指纹过滤（closable tag 提示） */
const fingerprintFilter = ref<string>()
const loginOptions = ref<string[]>([])
const hostOptions = ref<string[]>([])

const LOCK_TYPE_OPTIONS = [
  { value: 'keylock', label: 'KEY 锁' },
  { value: 'objectlock', label: 'OBJECT 锁' },
  { value: 'pagelock', label: 'PAGE 锁' },
  { value: 'ridlock', label: 'RID 锁' },
  { value: 'other', label: '其他' },
]

async function loadFilterOptions() {
  if (instanceId.value === undefined) return
  const { from, to } = fromTo()
  const opts = await getDeadlockFilters(instanceId.value, { from, to })
  loginOptions.value = opts.loginNames
  hostOptions.value = opts.hostNames
}

function applyFingerprint(fp: string) {
  fingerprintFilter.value = fingerprintFilter.value === fp ? undefined : fp
  search()
}

function clearFingerprint() {
  fingerprintFilter.value = undefined
  search()
}

/** 切实例：指纹/登录/主机是实例相关维度，清空后重查 */
function onInstanceChange() {
  fingerprintFilter.value = undefined
  loginName.value = undefined
  hostName.value = undefined
  search()
}

// ---------- 列表 + 展开 SPID 行级明细（懒加载详情接口） ----------
async function load() {
  if (instanceId.value === undefined) return
  if (!hasEvents.value) {
    if (hasTrendOnly.value) await loadMetricsTrend()
    return
  }
  loading.value = true
  try {
    const { from, to } = fromTo()
    const data = await getDeadlocks(instanceId.value, {
      page: query.page, limit: query.limit, from, to,
      fingerprint: fingerprintFilter.value,
      lockType: lockType.value,
      objectName: objectName.value || undefined,
      loginName: loginName.value,
      hostName: hostName.value,
    })
    rows.value = data.items
    total.value = data.total
    await Promise.all([loadTrend(), loadFingerprints(), loadFilterOptions()])
  } finally {
    loading.value = false
  }
}

function search() {
  query.page = 1
  expandedRowKeys.value = []
  load()
}

// 冷加载时能力矩阵/引擎身份晚于首跑 load() 到位（cap 未就绪按 full 兜底，PG 实例走错
// 完整形态分支被拒后无人补查）——到位/形态翻转时补查一次；flush post 保证 v-if 分支先挂载
watch(capsReady, v => { if (v) load() }, { flush: 'post' })
watch(hasTrendOnly, v => { if (v) load() }, { flush: 'post' })

function onPageChange(p: { current: number; pageSize: number }) {
  query.page = p.current
  query.limit = p.pageSize
  load()
}

function openDetail(id: number) {
  router.push({ name: 'deadlock-detail', params: { eventId: id } })
}

// 牺牲进程/涉及对象定宽 + scroll.x：长对象名（约束名/索引名）不约束会把表撑破容器
const VICTIM_COL_W = 240
const OBJECTS_COL_W = 520
const columns = [
  { title: '#', key: 'seq', width: 48 },
  { title: '死锁时间', key: 'time', width: TIME_COL_W },
  { title: '牺牲进程', key: 'victim', width: VICTIM_COL_W },
  { title: '参与方', dataIndex: 'processCount', width: 70 },
  { title: '涉及对象', key: 'objects', width: OBJECTS_COL_W },
  { title: '操作', key: 'op', width: 60 },
]

// ---------- 展开：SPID 行级明细（阿里云口径 14 列；Database 列不做——XE 只有库 id 无库名） ----------
const expandedRowKeys = ref<number[]>([])
const details = ref<Record<number, DeadlockDetail>>({})

async function onExpandChange(keys: (number | string)[]) {
  expandedRowKeys.value = keys as number[]
  await Promise.all((keys as number[]).filter(k => !details.value[k]).map(async (k) => {
    details.value[k] = await getDeadlockDetail(k)
  }))
}

/** 资源类型 → 中文描述（等待资源描述列） */
function typeDesc(t: string) {
  return ({ keylock: 'KEY 锁', objectlock: 'OBJECT 锁', pagelock: 'PAGE 锁', ridlock: 'RID 锁' } as Record<string, string>)[t.toLowerCase()] ?? t
}

interface ProcRow {
  id: string
  spid: number
  isVictim: boolean
  lastTranStarted?: string | null
  logUsed: number
  lockMode?: string | null
  waitResource?: string | null
  hostName?: string | null
  loginName?: string | null
  status?: string | null
  clientApp?: string | null
  inputBuf?: string | null
  /** 该进程申请的资源（waiter-list 命中） */
  requested: DeadlockResource[]
  /** 该进程持有的资源（owner-list 命中） */
  owned: DeadlockResource[]
}

/** 进程 ↔ 资源关联：持有/申请由 owners/waiters 的 processId 匹配 */
function procRows(detail: DeadlockDetail): ProcRow[] {
  return detail.processes.map(p => ({
    ...p,
    owned: detail.resources.filter(r => r.owners.some(o => o.processId === p.id)),
    requested: detail.resources.filter(r => r.waiters.some(w => w.processId === p.id)),
  }))
}

function resText(list: DeadlockResource[]) {
  return list.map(r => r.display).join(' ｜ ') || '-'
}

function waitDesc(row: ProcRow) {
  return [...new Set(row.requested.map(r => typeDesc(r.resourceType)))].join(' ｜ ') || '-'
}

const procColumns = [
  { title: '事务开始', key: 'lastTranStarted', width: TIME_COL_W },
  { title: 'SPID', dataIndex: 'spid', width: 55 },
  { title: '牺牲方', key: 'isVictim', width: 60 },
  { title: '日志(KB)', dataIndex: 'logUsed', width: 90 },
  { title: '锁模式', dataIndex: 'lockMode', width: 70 },
  { title: '等待资源描述', key: 'waitResourceDesc', width: 125 },
  { title: '持有对象', key: 'objectOwned', width: 180 },
  { title: '申请对象', key: 'objectRequested', width: 180 },
  { title: '等待资源', dataIndex: 'waitResource', width: 180 },
  { title: '主机名', dataIndex: 'hostName', width: 95 },
  { title: '登录名', dataIndex: 'loginName', width: 90 },
  { title: '状态', dataIndex: 'status', width: 80 },
  { title: '应用程序', dataIndex: 'clientApp', width: 105 },
  { title: 'SQL 文本', key: 'sqlText', width: 260 },
]

function procRowClass(record: ProcRow) {
  return record.isVictim ? 'victim-row' : ''
}

// keep-alive 切回：容器重新挂载，按当前尺寸重排（缓存期间窗口/侧栏变化的兜底）
onActivated(() => onResize())

onMounted(async () => {
  await init()
  window.addEventListener('resize', onResize)
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', onResize)
  trendChart?.dispose()
  trendChart = null
  rateChart?.dispose()
  rateChart = null
})
</script>

<template>
  <div class="page">
    <!-- 引擎守卫：事件与趋势数据源皆无的引擎整页空态（正常入口已被侧栏菜单隐藏，此为深链兜底） -->
    <a-empty v-if="!hasEvents && !hasTrendOnly" description="该引擎无死锁数据源" style="padding: 48px 0" />
    <a-card v-else>
      <!-- 工具栏（实例在顶栏全局选择） -->
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
        <a-button type="primary" @click="load">刷新</a-button>
      </div>

      <!-- 趋势-only 降级形态（无事件明细数据源的引擎）：死锁速率趋势（指标序列聚合，不伪造事件计数）。
           分支挂普通 div 而非 template fragment：template v-if 嵌套在 template v-else 分支内会触发
           Vue 3.5 片段 diff 错位（归并卡位置渲染出主表列头+残影，实测 5200 生产构建），div 包裹为最保守结构 -->
      <div v-if="!hasEvents">
        <div style="display: flex; align-items: baseline; margin-bottom: 4px">
          <span style="font-weight: 600">死锁趋势</span>
          <span class="text-tertiary" style="margin-left: 12px; font-size: 12px">
            该引擎无死锁事件明细数据源，展示实例指标采样的死锁速率聚合{{ rateAvg != null ? `（窗口均值 ${rateAvg.toFixed(4)} 次/秒）` : '' }}
          </span>
        </div>
        <div ref="rateEl" style="height: 260px"></div>
      </div>

      <!-- 完整形态（事件明细 + 锁类型分色 + 归并 + 过滤） -->
      <div v-else>

      <!-- 死锁趋势（锁资源类型分色） -->
      <div style="display: flex; align-items: baseline; margin-bottom: 4px">
        <span style="font-weight: 600">死锁趋势</span>
        <span class="text-tertiary" style="margin-left: 12px; font-size: 12px">
          死锁总数：{{ trendTotal.toLocaleString() }} 次
        </span>
      </div>
      <div ref="trendEl" style="height: 200px; margin-bottom: 12px"></div>

      <!-- 相似死锁归并 TOP（指纹归并） -->
      <template v-if="fingerprints.length">
        <div style="display: flex; align-items: baseline; margin-bottom: 4px">
          <span style="font-weight: 600">相似死锁归并</span>
          <span class="text-tertiary" style="margin-left: 12px; font-size: 12px">同构死锁（相同加锁冲突形态）合并计数，点击行按该形态过滤下方列表</span>
        </div>
        <a-table
          class="fp-table"
          :columns="fpColumns"
          :data-source="fingerprints"
          :pagination="false"
          size="small"
          row-key="fingerprint"
          style="margin-bottom: 16px"
          :custom-row="(r: DeadlockFingerprintStat) => ({ onClick: () => applyFingerprint(r.fingerprint) })"
          :row-class-name="(r: DeadlockFingerprintStat) => r.fingerprint === fingerprintFilter ? 'fp-active-row' : ''"
        >
          <template #bodyCell="{ column, record }">
            <template v-if="column.key === 'objects'">
              <EllipsisText :text="record.objects" viewer-title="死锁形态（涉及对象）" mono />
            </template>
            <template v-else-if="column.key === 'victim'">
              <span style="color: var(--text-secondary)"><EllipsisText :text="record.victimSummary" viewer-title="牺牲方" /></span>
            </template>
            <template v-else-if="column.key === 'first'">
              {{ fmtTime(record.firstTimeUtc) }}
            </template>
            <template v-else-if="column.key === 'last'">
              {{ fmtTime(record.lastTimeUtc) }}
            </template>
          </template>
        </a-table>
      </template>

      <!-- 列表过滤（只作用下方列表；归并卡点击带入的指纹也在此提示） -->
      <div class="toolbar" style="margin-bottom: 12px">
        <span>锁类型：</span>
        <a-select
          v-model:value="lockType"
          style="width: 120px"
          placeholder="锁类型"
          allow-clear
          :options="[{ value: '', label: '全部' }, ...LOCK_TYPE_OPTIONS]"
          @change="search"
        />
        <span>登录名：</span>
        <a-select
          v-model:value="loginName"
          style="width: 150px"
          placeholder="登录名"
          allow-clear
          show-search
          :options="[{ value: '', label: '全部' }, ...loginOptions.map(v => ({ value: v, label: v }))]"
          @change="search"
        />
        <span>主机名：</span>
        <a-select
          v-model:value="hostName"
          style="width: 160px"
          placeholder="主机名"
          allow-clear
          show-search
          :options="[{ value: '', label: '全部' }, ...hostOptions.map(v => ({ value: v, label: v }))]"
          @change="search"
        />
        <a-input-search
          v-model:value="objectName"
          style="width: 200px"
          placeholder="涉及对象（模糊）"
          allow-clear
          @search="search"
          @change="(_e: any) => objectName === '' && search()"
        />
        <a-tag v-if="fingerprintFilter" color="blue" closable @close.prevent="clearFingerprint">
          按死锁形态过滤：{{ fingerprints.find(f => f.fingerprint === fingerprintFilter)?.objects || fingerprintFilter }}
        </a-tag>
      </div>

      <!-- 死锁列表：点击行展开 SPID 行级明细 -->
      <a-table
        :columns="columns"
        :data-source="rows"
        :loading="loading"
        row-key="id"
        size="small"
        :scroll="{ x: 36 + 48 + VICTIM_COL_W + 70 + OBJECTS_COL_W + 60 + TIME_COL_W }"
        table-layout="fixed"
        :expanded-row-keys="expandedRowKeys"
        :expand-column-width="36"
        :pagination="{
          current: query.page,
          pageSize: query.limit,
          total,
          showSizeChanger: true,
          showTotal: (t: number) => `共 ${t} 条`,
        }"
        @change="onPageChange"
        @expand="(_e: boolean, r: DeadlockListItem) => onExpandChange(_e ? [...expandedRowKeys, r.id] : expandedRowKeys.filter(k => k !== r.id))"
      >
        <template #bodyCell="{ column, record, index }">
          <template v-if="column.key === 'seq'">
            {{ (query.page - 1) * query.limit + index + 1 }}
          </template>
          <template v-else-if="column.key === 'time'">
            {{ fmtTime(record.eventTimeUtc) }}
          </template>
          <template v-else-if="column.key === 'victim'">
            <a-tag color="red">牺牲 {{ record.victimSpids }}</a-tag>
            <!-- 摘要与 tag 同格并排：内联 max-width 强制截断（同 sqlText 列，列宽不自解析） -->
            <span style="color: var(--text-secondary)">　<EllipsisText :text="record.victimSummary" viewer-title="牺牲进程" style="max-width: 140px" /></span>
          </template>
          <template v-else-if="column.key === 'objects'">
            <EllipsisText :text="record.objects" viewer-title="涉及对象" mono />
          </template>
          <template v-else-if="column.key === 'op'">
            <a-button size="small" type="link" @click="openDetail(record.id)">详情</a-button>
          </template>
        </template>

        <!-- 展开行：每个参与进程一行（阿里云 SPID 明细口径） -->
        <template #expandedRowRender="{ record }">
          <template v-if="details[record.id]">
            <a-table
              :columns="procColumns"
              :data-source="procRows(details[record.id])"
              :pagination="false"
              size="small"
              row-key="id"
              :row-class-name="procRowClass"
              :scroll="{ x: 1570 + TIME_COL_W }"
              table-layout="fixed"
            >
              <template #bodyCell="{ column, record: proc }">
                <template v-if="column.key === 'lastTranStarted'">
                  {{ fmtTime(proc.lastTranStartedUtc) }}
                </template>
                <template v-else-if="column.key === 'waitResource'">
                  <EllipsisText :text="proc.waitResource" viewer-title="等待资源" mono />
                </template>
                <template v-else-if="column.key === 'hostName'">
                  <EllipsisText :text="proc.hostName" viewer-title="主机名" />
                </template>
                <template v-else-if="column.key === 'loginName'">
                  <EllipsisText :text="proc.loginName" viewer-title="登录名" />
                </template>
                <template v-else-if="column.key === 'status'">
                  <EllipsisText :text="proc.status" viewer-title="状态" />
                </template>
                <template v-else-if="column.key === 'clientApp'">
                  <EllipsisText :text="proc.clientApp" viewer-title="应用程序" />
                </template>
                <template v-else-if="column.key === 'isVictim'">
                  <a-tag v-if="proc.isVictim" color="red">牺牲</a-tag>
                  <span v-else class="text-disabled">-</span>
                </template>
                <template v-else-if="column.key === 'waitResourceDesc'">
                  {{ waitDesc(proc) }}
                </template>
                <template v-else-if="column.key === 'objectOwned'">
                  <EllipsisText :text="resText(proc.owned)" viewer-title="持有对象" mono />
                </template>
                <template v-else-if="column.key === 'objectRequested'">
                  <EllipsisText :text="resText(proc.requested)" viewer-title="申请对象" mono />
                </template>
                <template v-else-if="column.key === 'sqlText'">
                  <!-- 内联 max-width 强制截断：不依赖嵌套展开表 td 宽度解析，点击弹全文 -->
                  <EllipsisText :text="proc.inputBuf" viewer-title="SQL 文本（inputbuf）" mono style="max-width: 236px" />
                </template>
              </template>
            </a-table>
          </template>
          <a-spin v-else size="small" style="display: block; padding: 8px 0" />
        </template>
      </a-table>
      </div>
    </a-card>
  </div>
</template>

<style scoped>
:deep(.victim-row) {
  background: var(--danger-bg);
}

/* 归并卡：可点击行 + 当前过滤形态高亮 */
:deep(.fp-table .ant-table-tbody > tr) {
  cursor: pointer;
}

:deep(.fp-active-row) {
  background: var(--color-primary-bg) !important;
}
</style>
