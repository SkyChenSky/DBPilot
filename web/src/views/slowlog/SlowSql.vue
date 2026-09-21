<script setup lang="ts">
import { computed, nextTick, onActivated, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import dayjs from 'dayjs'
import { graphic, type ECharts } from 'echarts/core'
import {
  getSlowSqlDetail,
  getSlowSqlList,
  getSlowSqlTemplates,
  getSlowSqlTrend,
  type SlowSqlDetail,
  type SlowSqlItem,
  type SlowSqlTemplate,
} from '../../api/slowsql'
import EllipsisText from '../../components/EllipsisText.vue'
import SqlDetailModal from '../../components/SqlDetailModal.vue'
import { CHART_COLORS, initChart, rebindChart } from '../../charts/echarts'
import { useInstanceDatabases } from '../../composables/useInstanceDatabases'
import { useTimeRange } from '../../composables/useTimeRange'
import { fitColumnWidth, TIME_COL_W } from '../../utils/fitColumnWidth'
import { fmtAvg, fmtNum, fmtSec, fmtTime } from '../../utils/format'
import { CAP, useEngineCaps } from '../../api/engine'

// 实例来自顶栏全局上下文：首载/切换 → 重拉库列表 + 按当前 tab 重查 + 更新采集状态标注
const { instances, instanceId, dbOptions, dbName, dbLoading, init } = useInstanceDatabases(async () => {
  captureError.value = hasEvents.value
    ? instances.value.find(i => i.id === instanceId.value)?.lastError ?? undefined
    : undefined
  await load()
})

// 引擎能力（能力矩阵驱动）：完整形态 = 事件明细 + 模板统计（slowSqlEvents，XE/slow_log 通道）；
// 无事件通道的引擎（PostgreSQL）降级为 Top SQL 累计模板榜（slowSqlTemplates，读 top_sql_delta 聚合，
// 绝不伪造 slow_sql 事件行）——隐藏明细 tab / 趋势小图 / 采集状态标注（采集 Job 不跑）
const { cap } = useEngineCaps()
const hasEvents = computed(() => cap(CAP.slowSqlEvents) !== 'none')
const degraded = computed(() => !hasEvents.value && cap(CAP.slowSqlTemplates) !== 'none')
const loading = ref(false)

/** 筛选：时间档位默认近 1h + 库 + 最小耗时（档位↔自定义联动统一走 useTimeRange） */
const { rangeKey, rangeOptions, customRange, onPresetChange, onCustomChange, fromTo, disabledDate } = useTimeRange({ defaultKey: '1h', onApply: () => search() })
const filterMinMs = ref<number>()
/** 排除系统库（选具体库时无效，口径对齐 Top SQL） */
const excludeSystemDb = ref(false)

/** 下传 API 的筛选：选了库时排除系统库开关不生效 */
function dbFilters() {
  const db = dbName.value || undefined
  return { db, excludeSystemDb: !db && excludeSystemDb.value }
}

/** 当前实例的采集状态标注（lastError 由 SlowSqlCollectService 写入） */
const captureError = ref<string>()

// ---------- 明细 / 模板 tab ----------
/** 默认聚合统计视图（先看整体分布再下钻明细） */
const tab = ref('template')
const rows = ref<SlowSqlItem[]>([])
const total = ref(0)
const query = reactive({ page: 1, limit: 20 })

/** 模板详情入口：按指纹过滤明细 */
const activeFingerprint = ref<string>()
const templates = ref<SlowSqlTemplate[]>()

async function load() {
  if (instanceId.value === undefined) return
  if (!hasEvents.value && tab.value === 'detail') return
  loading.value = true
  try {
    const { from, to } = fromTo()
    const { db, excludeSystemDb: exclude } = dbFilters()
    if (tab.value === 'detail') {
      const data = await getSlowSqlList(instanceId.value, {
        page: query.page, limit: query.limit, from, to,
        db,
        minDurationMs: filterMinMs.value || undefined,
        fingerprint: activeFingerprint.value || undefined,
        excludeSystemDb: exclude,
      })
      rows.value = data.items
      total.value = data.total
    } else {
      templates.value = await getSlowSqlTemplates(instanceId.value, {
        from, to,
        db,
        minDurationMs: filterMinMs.value || undefined,
        excludeSystemDb: exclude,
      })
    }
    // 趋势小图仅事件形态有数据源（降级形态调用必被拒）
    if (hasEvents.value) loadTrend()
  } finally {
    loading.value = false
  }
}

function search() {
  query.page = 1
  activeFingerprint.value = undefined
  load()
}

function onPageChange(p: { current: number; pageSize: number }) {
  query.page = p.current
  query.limit = p.pageSize
  load()
}

/** 模板 → 明细：带指纹过滤切 tab */
function openTemplate(fp: string) {
  activeFingerprint.value = fp
  tab.value = 'detail'
  query.page = 1
  nextTick(() => load())
}

function clearFingerprint() {
  activeFingerprint.value = undefined
  query.page = 1
  load()
}

// ---------- 趋势小图（条数柱 + 总耗时长线双轴） ----------
const trendEl = ref<HTMLDivElement>()
let trendChart: ECharts | null = null
/** 当前筛选口径下的慢日志总数（趋势点 count 求和） */
const trendTotal = ref(0)

async function loadTrend() {
  if (instanceId.value === undefined || !trendEl.value) return
  const { from, to } = fromTo()
  const { db, excludeSystemDb: exclude } = dbFilters()
  const points = await getSlowSqlTrend(instanceId.value, {
    from, to, db,
    minDurationMs: filterMinMs.value || undefined,
    fingerprint: activeFingerprint.value || undefined,
    excludeSystemDb: exclude,
  })
  trendTotal.value = points.reduce((s, p) => s + p.count, 0)

  trendChart = rebindChart(trendChart, trendEl.value) ?? initChart(trendEl.value)
  trendChart.setOption({
    grid: { left: 64, right: 56, top: 36, bottom: 28 },
    tooltip: {
      trigger: 'axis',
      axisPointer: { type: 'line' },
      valueFormatter: (v: unknown) => (v == null ? '-' : String(v)),
    },
    legend: {
      data: ['总耗时(秒)', '慢日志条数'], top: 0, left: 0,
      itemWidth: 14, textStyle: { fontSize: 11 },
    },
    xAxis: {
      type: 'category',
      data: points.map(p => dayjs(p.timeUtc).format('HH:mm')),
      axisTick: { alignWithLabel: true },
      axisLabel: { fontSize: 11 },
    },
    yAxis: [
      {
        type: 'value',
        axisLabel: {
          fontSize: 11,
          formatter: (v: number) => (v >= 1000 ? `${Math.round(v / 1000)}k` : `${v}`),
        },
      },
      {
        type: 'value',
        axisLabel: { fontSize: 11 },
        splitLine: { show: false },
      },
    ],
    series: [
      {
        name: '总耗时(秒)', type: 'line', smooth: true,
        data: points.map(p => Math.round(p.totalMs / 100) / 10),
        lineStyle: { width: 2 }, itemStyle: { color: CHART_COLORS.primary }, symbol: 'circle', symbolSize: 4, showSymbol: false,
        areaStyle: {
          // 主色渐变淡出（8 位 hex = #1677ff + 透明度后缀）
          color: new graphic.LinearGradient(0, 0, 0, 1, [
            { offset: 0, color: `${CHART_COLORS.primary}2e` },
            { offset: 1, color: `${CHART_COLORS.primary}05` },
          ]),
        },
        markLine: {
          silent: true, symbol: 'none',
          data: [{ type: 'average', name: '均值' }],
          lineStyle: { color: CHART_COLORS.primary, type: 'dashed', opacity: 0.5 },
          label: { formatter: '均值 {c}', fontSize: 10, color: CHART_COLORS.primary },
        },
      },
      {
        name: '慢日志条数', type: 'bar', yAxisIndex: 1, data: points.map(p => p.count),
        barMaxWidth: 14, itemStyle: { color: `${CHART_COLORS.warning}bf`, borderRadius: [2, 2, 0, 0] },
      },
    ],
  }, true)
}

function onResize() {
  trendChart && !trendChart.isDisposed() && trendChart.resize()
}

// ---------- SQL 全文弹窗 ----------
const sqlOpen = ref(false)
const sqlFull = ref<SlowSqlDetail>()

/** 统计视图 SQL 样例弹窗（样例为聚合 SQL 前 500 字符） */
const sampleOpen = ref(false)
const sampleText = ref('')

async function showSql(rowId: number) {
  sqlFull.value = await getSlowSqlDetail(rowId)
  sqlOpen.value = true
}

function fmtRatio(r?: number | null) {
  return r == null ? '-' : `${r.toFixed(1)}%`
}

// 列宽自适应：名称类列按当前数据最长值量宽（带上限，超出点击看全）
const detailDbW = computed(() => fitColumnWidth(rows.value.map(r => r.dbName), { min: 90 }))
const templateDbW = computed(() => fitColumnWidth((templates.value ?? []).map(t => t.dbName), { min: 90 }))
const userW = computed(() => fitColumnWidth(rows.value.map(r => r.loginName), { min: 95, max: 200 }))
const hostW = computed(() => fitColumnWidth(rows.value.map(r => r.hostName), { min: 110, max: 220 }))
const appW = computed(() => fitColumnWidth(rows.value.map(r => r.appName), { min: 110, max: 220 }))

const detailColumns = computed(() => [
  { title: '#', key: 'seq', width: 48, fixed: 'left' },
  { title: 'SQL ID', key: 'sqlid', width: 130, fixed: 'left' },
  { title: 'SQL', key: 'sql', width: 180 },
  { title: '执行完成时间', key: 'time', width: TIME_COL_W },
  { title: '数据库', dataIndex: 'dbName', width: detailDbW.value },
  { title: '耗时(s)', key: 'dur', width: 95 },
  { title: 'CPU(s)', key: 'cpu', width: 95 },
  { title: '影响行', key: 'rows', width: 80 },
  { title: '总逻辑读', key: 'reads', width: 85 },
  { title: '总物理读', key: 'preads', width: 85 },
  { title: '总I/O写', key: 'writes', width: 80 },
  { title: '用户', key: 'user', width: userW.value },
  { title: '访问来源', key: 'host', width: hostW.value },
  { title: '应用名', key: 'app', width: appW.value },
])

const templateColumns = computed(() => [
  { title: '#', key: 'seq', width: 45, fixed: 'left' },
  { title: 'SQL 样例', key: 'sample', width: 240, fixed: 'left' },
  { title: '数据库', dataIndex: 'dbName', width: templateDbW.value },
  { title: '执行次数', dataIndex: 'count', width: 70 },
  { title: '耗时比例', key: 'ratio', width: 80 },
  { title: '平均耗时(s)', key: 'avg', width: 100 },
  { title: '最大耗时(s)', key: 'max', width: 100 },
  { title: '总CPU(s)', key: 'cpuTotal', width: 95 },
  { title: '平均CPU(s)', key: 'cpuAvg', width: 95 },
  { title: '最大CPU(s)', key: 'cpuMax', width: 95 },
  { title: '平均影响行', key: 'rowsAvg', width: 80 },
  { title: '最大影响行', key: 'rowsMax', width: 80 },
  { title: '总逻辑读', key: 'readsTotal', width: 85 },
  { title: '平均逻辑读', key: 'readsAvg', width: 85 },
  { title: '最大逻辑读', key: 'readsMax', width: 85 },
  { title: '总物理读', key: 'preadsTotal', width: 85 },
  { title: '平均物理读', key: 'preadsAvg', width: 85 },
  { title: '最大物理读', key: 'preadsMax', width: 85 },
  { title: '总I/O写', key: 'writesTotal', width: 80 },
  { title: '平均I/O写', key: 'writesAvg', width: 80 },
  { title: '最大I/O写', key: 'writesMax', width: 80 },
  // 「查看明细」下钻仅事件形态可用（降级模板榜无明细行）
  ...(hasEvents.value ? [{ title: '操作', key: 'op', width: 80, fixed: 'right' as const }] : []),
])

watch(tab, () => load())

// 切到无事件明细数据源的引擎：停在明细 tab 时回退统计视图（明细入口已隐藏，防切引擎残留）
watch(hasEvents, (v) => {
  if (!v && tab.value === 'detail') tab.value = 'template'
})

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
})
</script>

<template>
  <div class="page">
    <a-card>
      <!-- 工具栏（实例在顶栏全局选择，页内不再重复） -->
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
        <span>库：</span>
        <a-select
          v-model:value="dbName"
          style="width: 200px"
          placeholder="全部库"
          :loading="dbLoading"
          :options="dbOptions"
          @change="search"
        />
        <a-tooltip title="全部库视图下额外排除 master/model/msdb/tempdb 的慢SQL（选具体库时无效）">
          <a-switch
            v-model:checked="excludeSystemDb"
            checked-children="排除系统库"
            un-checked-children="含系统库"
            :disabled="!!dbName"
            @change="search"
          />
        </a-tooltip>
      </div>
      <!-- 最小耗时单独一行（块级 div 工具栏，相邻不会换行）；降级形态隐藏（累计模板无单次耗时语义） -->
      <div class="toolbar" style="margin-bottom: 12px">
        <template v-if="hasEvents">
          <span>最小耗时(ms)：</span>
          <a-input-number v-model:value="filterMinMs" :min="1" style="width: 110px" placeholder="不限" @press-enter="search" />
        </template>
        <a-button type="primary" @click="search">查询</a-button>
      </div>

      <!-- 采集状态标注（降级形态采集 Job 不跑，无 last_error 语义） -->
      <a-alert
        v-if="captureError"
        type="warning"
        show-icon
        :message="captureError"
        style="margin-bottom: 12px"
      />

      <!-- 降级提示：无慢日志事件通道的引擎展示 Top SQL 累计模板榜 -->
      <a-alert
        v-if="degraded"
        type="info"
        show-icon
        message="该引擎无慢日志事件明细数据源，以下为 Top SQL 累计模板榜（按执行耗时聚合的近似视图）"
        style="margin-bottom: 12px"
      />

      <!-- 量级趋势小图（事件通道聚合，降级形态无此数据源） -->
      <template v-if="hasEvents">
        <div style="display: flex; align-items: baseline; margin-bottom: 4px">
          <span style="font-weight: 600">慢日志趋势</span>
          <span class="text-tertiary" style="margin-left: 12px; font-size: 12px">
            慢日志总数：{{ trendTotal.toLocaleString() }} 条
          </span>
        </div>
        <div ref="trendEl" style="height: 200px; margin-bottom: 12px"></div>
      </template>

      <a-tabs v-model:active-key="tab">
        <!-- 统计视图（默认）：指纹聚合分布，下钻明细 -->
        <a-tab-pane key="template" :tab="hasEvents ? '慢日志统计' : '慢 SQL 模板（Top SQL 累计）'">
          <a-table
            :columns="templateColumns"
            :data-source="templates"
            :loading="loading"
            row-key="fingerprint"
            size="small"
            :scroll="{ x: 1920 + templateDbW }"
            :pagination="false"
          >
            <template #bodyCell="{ column, record, index }">
              <template v-if="column.key === 'seq'">{{ index + 1 }}</template>
              <template v-else-if="column.key === 'sample'">
                <a class="fixed-cell-sql" :title="record.sampleSql" @click="sampleText = record.sampleSql; sampleOpen = true">{{ record.sampleSql }}</a>
              </template>
              <template v-else-if="column.key === 'ratio'">
                <span style="color: var(--danger-text); font-weight: 600">{{ fmtRatio(record.totalRatio) }}</span>
              </template>
              <template v-else-if="column.key === 'avg'">{{ fmtSec(record.avgMs) }}</template>
              <template v-else-if="column.key === 'max'">{{ fmtSec(record.maxMs) }}</template>
              <template v-else-if="column.key === 'cpuTotal'">{{ fmtSec(record.cpuTotalMs) }}</template>
              <template v-else-if="column.key === 'cpuAvg'">{{ fmtSec(record.cpuAvgMs) }}</template>
              <template v-else-if="column.key === 'cpuMax'">{{ fmtSec(record.cpuMaxMs) }}</template>
              <template v-else-if="column.key === 'rowsAvg'">{{ fmtAvg(record.rowsAvg) }}</template>
              <template v-else-if="column.key === 'rowsMax'">{{ fmtNum(record.rowsMax) }}</template>
              <template v-else-if="column.key === 'readsTotal'">{{ fmtNum(record.readsTotal) }}</template>
              <template v-else-if="column.key === 'readsAvg'">{{ fmtAvg(record.readsAvg) }}</template>
              <template v-else-if="column.key === 'readsMax'">{{ fmtNum(record.readsMax) }}</template>
              <template v-else-if="column.key === 'preadsTotal'">{{ fmtNum(record.preadsTotal) }}</template>
              <template v-else-if="column.key === 'preadsAvg'">{{ fmtAvg(record.preadsAvg) }}</template>
              <template v-else-if="column.key === 'preadsMax'">{{ fmtNum(record.preadsMax) }}</template>
              <template v-else-if="column.key === 'writesTotal'">{{ fmtNum(record.writesTotal) }}</template>
              <template v-else-if="column.key === 'writesAvg'">{{ fmtAvg(record.writesAvg) }}</template>
              <template v-else-if="column.key === 'writesMax'">{{ fmtNum(record.writesMax) }}</template>
              <template v-else-if="column.key === 'op'">
                <a-button size="small" type="link" @click="openTemplate(record.fingerprint)">查看明细</a-button>
              </template>
            </template>
          </a-table>
        </a-tab-pane>

        <!-- 明细视图（事件通道形态） -->
        <a-tab-pane v-if="hasEvents" key="detail" tab="慢日志明细">
          <div v-if="activeFingerprint" style="margin-bottom: 8px">
            <a-tag color="blue" closable @close="clearFingerprint">
              指纹 {{ activeFingerprint }}
            </a-tag>
            <span class="text-tertiary" style="font-size: 12px">模板过滤中</span>
          </div>
          <a-table
            :columns="detailColumns"
            :data-source="rows"
            :loading="loading"
            row-key="id"
            size="small"
            :scroll="{ x: 878 + TIME_COL_W + detailDbW + userW + hostW + appW }"
            :pagination="{
              current: query.page,
              pageSize: query.limit,
              total,
              showSizeChanger: true,
              showTotal: (t: number) => `共 ${t} 条`,
            }"
            @change="onPageChange"
          >
            <template #bodyCell="{ column, record, index }">
              <template v-if="column.key === 'seq'">
                {{ (query.page - 1) * query.limit + index + 1 }}
              </template>
              <template v-else-if="column.key === 'time'">
                {{ fmtTime(record.eventTimeUtc) }}
              </template>
              <template v-else-if="column.key === 'sqlid'">
                <span style="font-family: consolas, monospace; font-size: 12px" :title="record.fingerprint ?? ''">
                  {{ record.fingerprint ?? '-' }}
                </span>
              </template>
              <template v-else-if="column.key === 'user'">
                <EllipsisText :text="record.loginName" viewer-title="用户" />
              </template>
              <template v-else-if="column.key === 'host'">
                <EllipsisText :text="record.hostName" viewer-title="访问来源" />
              </template>
              <template v-else-if="column.key === 'app'">
                <EllipsisText :text="record.appName" viewer-title="应用名" />
              </template>
              <template v-else-if="column.key === 'dur'">
                <span style="color: var(--danger-text); font-weight: 600">{{ fmtSec(record.durationMs) }}</span>
              </template>
              <template v-else-if="column.key === 'cpu'">
                {{ fmtSec(record.cpuMs) }}
              </template>
              <template v-else-if="column.key === 'rows'">
                {{ fmtNum(record.rowCount) }}
              </template>
              <template v-else-if="column.key === 'reads'">
                {{ fmtNum(record.logicalReads) }}
              </template>
              <template v-else-if="column.key === 'preads'">
                {{ fmtNum(record.physicalReads) }}
              </template>
              <template v-else-if="column.key === 'writes'">
                {{ fmtNum(record.writes) }}
              </template>
              <template v-else-if="column.key === 'sql'">
                <a class="sql-preview" :title="record.sqlPreview || '(空)'" @click="showSql(record.id)">{{ record.sqlPreview || '(空)' }}</a>
              </template>
            </template>
          </a-table>
        </a-tab-pane>
      </a-tabs>
    </a-card>

    <!-- SQL 全文弹窗 -->
    <SqlDetailModal v-model:open="sqlOpen" title="SQL 全文" :sql="sqlFull?.sqlText ?? ''">
      <template v-if="sqlFull" #meta>
        <a-descriptions :column="4" size="small">
          <a-descriptions-item label="时间">{{ fmtTime(sqlFull.eventTimeUtc) }}</a-descriptions-item>
          <a-descriptions-item label="库">{{ sqlFull.dbName ?? '-' }}</a-descriptions-item>
          <a-descriptions-item label="耗时(秒)">{{ fmtSec(sqlFull.durationMs) }}</a-descriptions-item>
          <a-descriptions-item label="指纹">
            <span style="font-family: consolas, monospace">{{ sqlFull.fingerprint ?? '-' }}</span>
          </a-descriptions-item>
        </a-descriptions>
      </template>
    </SqlDetailModal>

    <!-- 统计视图 SQL 样例弹窗 -->
    <SqlDetailModal v-model:open="sampleOpen" title="SQL 样例" :sql="sampleText" />
  </div>
</template>

<style scoped>
/* 冻结列内 SQL 样例：max-width 跟随列宽（table-layout fixed 下即列宽 280px），
   避免 320px 固定截断宽超出冻结列遮挡右侧列 */
.fixed-cell-sql {
  display: inline-block;
  max-width: 100%;
  font-family: consolas, monospace;
  font-size: 12px;
  color: var(--color-primary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  vertical-align: bottom;
  cursor: pointer;
}
.sql-preview {
  display: inline-block;
  /* 固定截断宽度须小于列宽（200 - 单元格左右 padding 16），列被拉伸或横滚时都不溢出遮挡 */
  max-width: 184px;
  font-family: consolas, monospace;
  font-size: 12px;
  color: var(--color-primary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  vertical-align: bottom;
  cursor: pointer;
}
</style>
