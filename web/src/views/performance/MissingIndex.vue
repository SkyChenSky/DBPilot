<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { message } from 'ant-design-vue'
import dayjs from 'dayjs'
import {
  getMissingIndexSnapshots,
  getMissingIndexTrend,
  recollectMissingIndexes,
  type MissingIndexSnapshotItem,
  type MissingIndexSnapshotResult,
  type MissingIndexTrendPoint,
} from '../../api/indexDiag'
import EllipsisText from '../../components/EllipsisText.vue'
import StatCard from '../../components/StatCard.vue'
import { CHART_COLORS } from '../../charts/echarts'
import { useInstanceDatabases } from '../../composables/useInstanceDatabases'
import { useCharts } from '../../composables/useChart'
import { fitColumnWidth, MINUTE_COL_W } from '../../utils/fitColumnWidth'
import { fmtTimeMinute } from '../../utils/format'
import { CAP, useEngineCaps } from '../../api/engine'

// 实例来自顶栏全局上下文：首载/切换 → 重拉库列表 + 重查快照
const { instanceId, dbOptions, dbName, dbLoading, init } = useInstanceDatabases(() => loadSnapshot())

// 引擎守卫（能力矩阵）：缺失索引建议仅支持有对应数据源的引擎——整页空态、不发请求；切回自动恢复
const { cap } = useEngineCaps()
const unsupported = computed(() => cap(CAP.missingIndex) === 'none')

const loading = ref(false)
const excludeSystemDb = ref(false)
const snapshotResult = ref<MissingIndexSnapshotResult>()
const page = ref(1)
const onlyNew = ref(false)

const overview = computed(() => snapshotResult.value?.overview)

const items = computed<MissingIndexSnapshotItem[]>(() => snapshotResult.value?.items ?? [])

// 阿里云同款过滤条件（默认全开，可勾选）
const filters = reactive({
  minTablePages: true,     // 表 ≥ 100 页
  minTableRows: true,      // 表 ≥ 1000 条
  minSeeks: true,          // 索引 ≥ 100 次查找
  minImpact: true,         // 性能提升 ≥ 10%
  maxKeyColumns: true,     // 索引字段个数 ≥ 7 的剔除（键过宽）
})

function keyColumnCount(i: { equalityColumns?: string | null; inequalityColumns?: string | null }) {
  return (i.equalityColumns?.split(',').filter(s => s.trim()).length ?? 0)
    + (i.inequalityColumns?.split(',').filter(s => s.trim()).length ?? 0)
}

const filteredItems = computed(() => {
  return items.value.filter(i => {
    if (onlyNew.value && !i.isNew) return false
    if (filters.minTablePages && (i.tablePages ?? 0) < 100) return false
    if (filters.minTableRows && (i.tableRows ?? 0) < 1000) return false
    if (filters.minSeeks && i.userSeeks < 100) return false
    if (filters.minImpact && i.avgUserImpact < 10) return false
    if (filters.maxKeyColumns && keyColumnCount(i) >= 7) return false
    return true
  })
})

// ---------- 四张分布图卡（对齐阿里云；数据随当前模式 items / 每日快照趋势联动） ----------

/** 缺失索引变化趋势：每个快照批次的建议条数（独立于模式，来自每日快照） */
const trendPoints = ref<MissingIndexTrendPoint[]>([])

/** 用户最后查找时间分布：近一天 / 近一周 / 近两周 / 近一月 / 更早 */
const seekData = computed(() => {
  const b = [0, 0, 0, 0, 0]
  const now = Date.now()
  for (const i of items.value) {
    const days = i.lastUserSeek ? (now - dayjs(i.lastUserSeek).valueOf()) / 86400000 : Infinity
    if (days <= 1) b[0]++
    else if (days <= 7) b[1]++
    else if (days <= 14) b[2]++
    else if (days <= 30) b[3]++
    else b[4]++
  }
  return b
})

/** 查询开销平均减少分布（avg_user_impact 即开销可减少比例） */
const costData = computed(() => {
  const b = [0, 0, 0, 0] // ≤10 / 10~40 / 40~70 / >70
  for (const i of items.value) {
    const v = i.avgUserImpact
    if (v <= 10) b[0]++
    else if (v <= 40) b[1]++
    else if (v <= 70) b[2]++
    else b[3]++
  }
  return b
})

/** 查询性能提升分布 */
const gainData = computed(() => {
  const b = [0, 0, 0, 0] // <80 / 80~90 / 90~99 / 100
  for (const i of items.value) {
    const v = i.avgUserImpact
    if (v < 80) b[0]++
    else if (v < 90) b[1]++
    else if (v < 100) b[2]++
    else b[3]++
  }
  return b
})

/** 四张分布图卡（对齐阿里云）：生命周期统一 useCharts——图卡行有 v-if（overview 瞬时为空会卸载重建 div），
 * 函数 ref 绑定天然处理卸载 dispose / 重挂补建，RO 自动 resize + keep-alive 兜底。 */
const { bind, renderVisible } = useCharts(key => barOption(key))

function barOption(key: string) {
  let categories: string[]
  let values: number[]
  if (key === 'trend') {
    // 同日多批次（凌晨 Job + 手动补采）时标签带时刻，否则只显示日期
    const days = new Set(trendPoints.value.map(p => dayjs(p.snapshotTimeUtc).format('MM-DD')))
    const fmt = days.size < trendPoints.value.length ? 'MM-DD HH:mm' : 'MM-DD'
    categories = trendPoints.value.map(p => dayjs(p.snapshotTimeUtc).format(fmt))
    values = trendPoints.value.map(p => p.count)
  }
  else if (key === 'seek') {
    categories = ['近一天', '近一周', '近两周', '近一月', '更早']
    values = seekData.value
  }
  else if (key === 'cost') {
    categories = ['≤10%', '10~40%', '40~70%', '>70%']
    values = costData.value
  }
  else {
    categories = ['<80%', '80~90%', '90~99%', '100%']
    values = gainData.value
  }
  if (!values.length) return null
  return {
    grid: { left: 36, right: 8, top: 12, bottom: 26 },
    tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' } },
    xAxis: {
      type: 'category',
      data: categories,
      axisTick: { alignWithLabel: true },
      axisLabel: { fontSize: 11 },
    },
    yAxis: {
      type: 'value',
      minInterval: 1,
      axisLabel: { fontSize: 11 },
    },
    series: [{ type: 'bar' as const, data: values, barMaxWidth: 28, itemStyle: { color: CHART_COLORS.primary } }],
  }
}

watch([trendPoints, seekData, costData, gainData], () => renderVisible(), { flush: 'post' })

// ---------- 表格数值条形（蓝色横条指示相对大小，对齐阿里云） ----------
const maxSeeks = computed(() => Math.max(1, ...filteredItems.value.map(i => i.userSeeks)))
const maxScore = computed(() => Math.max(1, ...filteredItems.value.map(i => i.score)))

function barWidth(v: number, max: number) {
  return `${Math.min(100, Math.round((v / max) * 100))}%`
}

/** 加载最新快照 */
async function loadSnapshot() {
  if (unsupported.value) return
  if (!instanceId.value && instanceId.value !== 0) return
  loading.value = true
  try {
    snapshotResult.value = await getMissingIndexSnapshots(instanceId.value, dbName.value ?? '', excludeSystemDb.value)
    trendPoints.value = await getMissingIndexTrend(instanceId.value, dbName.value ?? '', excludeSystemDb.value)
  } finally {
    loading.value = false
  }
}

/** 立即重新采集（与每日 03:10 Job 同一代码路径，落库后刷新；后端有冷却窗口防频繁采集） */
async function recollect() {
  if (!instanceId.value && instanceId.value !== 0) return
  loading.value = true
  try {
    await recollectMissingIndexes(instanceId.value)
    await loadSnapshot()
    message.success('重新采集完成')
  } catch {
    // 冷却拒绝/采集失败：拦截器已弹错误提示
  } finally {
    loading.value = false
  }
}

async function copySql(sql: string) {
  try {
    await navigator.clipboard.writeText(sql)
    message.success('脚本已复制')
  } catch {
    message.error('复制失败，请手动选择复制')
  }
}

/** 一键导出过滤后全部创建脚本（.sql 下载） */
function exportAll() {
  if (!filteredItems.value.length) return
  const text = filteredItems.value
    .map(i => `-- ${i.dbName} | 评分 ${i.score.toFixed(2)} | seeks ${i.userSeeks}\n${i.createIndexSql}`)
    .join('\n\n')
  const useClause = dbName.value ? `USE [${dbName.value}];\nGO\n\n` : ''
  const blob = new Blob([`${useClause}${text}\n`], { type: 'text/plain;charset=utf-8' })
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = `missing_indexes_${dbName.value || 'all'}.sql`
  a.click()
  URL.revokeObjectURL(a.href)
}

// 名称/列清单按过滤后数据最长值量宽（带上限，超出点击看全）
const tableColW = computed(() => fitColumnWidth(filteredItems.value.map(i => `${i.dbName}.${i.tableName}`), { min: 150, max: 240 }))
const eqColW = computed(() => fitColumnWidth(filteredItems.value.map(i => i.equalityColumns), { min: 105, max: 220 }))
const ineqColW = computed(() => fitColumnWidth(filteredItems.value.map(i => i.inequalityColumns), { min: 105, max: 220 }))
const incColW = computed(() => fitColumnWidth(filteredItems.value.map(i => i.includedColumns), { min: 105, max: 220 }))

// 图表 resize/dispose/keep-alive 兜底全部由 useCharts 托管

onMounted(async () => {
  // 打开页面默认查询：全局实例 + "全部库"（composable 默认口径）
  await init()
})
</script>

<template>
  <div class="page">
    <!-- 引擎守卫：MySQL 实例无缺失索引数据源，整页空态不发请求 -->
    <a-empty v-if="unsupported" description="该引擎不支持缺失索引诊断" style="padding: 48px 0" />
    <a-card v-else>
      <!-- 工具栏：库 + 查询（实例在顶栏全局选择） -->
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
        <a-tooltip title="全部库视图下额外排除 master/model/msdb/tempdb 的建议（选具体库时无效）">
          <a-switch
            v-model:checked="excludeSystemDb"
            checked-children="排除系统库"
            un-checked-children="含系统库"
            :disabled="!!dbName"
            @change="loadSnapshot"
          />
        </a-tooltip>
        <a-button type="primary" :loading="loading" :disabled="!instanceId" @click="recollect">重新采集</a-button>
        <a-button :disabled="!filteredItems.length" @click="exportAll">导出脚本</a-button>
        <a-checkbox v-model:checked="onlyNew">仅看近 7 天新增</a-checkbox>
        <span class="text-tertiary">
          上次采集：{{ snapshotResult?.snapshotTimeUtc ? fmtTimeMinute(snapshotResult.snapshotTimeUtc) : '暂无快照（每日 03:10 采集）' }}
        </span>
      </div>

      <!-- 总览统计（对齐阿里云） -->
      <div v-if="overview" class="overview-grid">
        <StatCard title="索引缺失总量" :value="overview.total" unit="条" />
        <StatCard title="性能提升大于80%" :value="`${overview.highImpact} 条（${overview.highImpactPercent}%）`" />
        <StatCard title="近一天访问" :value="`${overview.lastDayCount} 条（${overview.lastDayPercent}%）`" />
        <StatCard title="近一周访问" :value="`${overview.lastWeekCount} 条（${overview.lastWeekPercent}%）`" />
        <StatCard title="近一月访问" :value="`${overview.lastMonthCount} 条（${overview.lastMonthPercent}%）`" />
      </div>

      <!-- 四张分布图卡（对齐阿里云） -->
      <a-row v-if="overview" :gutter="12" style="margin-bottom: 16px">
        <a-col :span="6">
          <a-card size="small" title="索引缺失变化趋势">
            <div :ref="bind('trend')" style="height: 160px"></div>
          </a-card>
        </a-col>
        <a-col :span="6">
          <a-card size="small" title="用户最后查找时间">
            <div :ref="bind('seek')" style="height: 160px"></div>
          </a-card>
        </a-col>
        <a-col :span="6">
          <a-card size="small" title="查询开销平均减少">
            <div :ref="bind('cost')" style="height: 160px"></div>
          </a-card>
        </a-col>
        <a-col :span="6">
          <a-card size="small" title="查询性能提升">
            <div :ref="bind('gain')" style="height: 160px"></div>
          </a-card>
        </a-col>
      </a-row>

      <!-- 过滤条件 -->
      <div class="toolbar" style="margin-bottom: 12px">
        <span class="text-tertiary">已过滤：</span>
        <a-checkbox v-model:checked="filters.minTablePages">表 ≥ 100页</a-checkbox>
        <a-checkbox v-model:checked="filters.minTableRows">表 ≥ 1000条</a-checkbox>
        <a-checkbox v-model:checked="filters.minSeeks">索引 ≥ 100次查找</a-checkbox>
        <a-checkbox v-model:checked="filters.minImpact">索引 ≥ 10%性能提升</a-checkbox>
        <a-checkbox v-model:checked="filters.maxKeyColumns">剔除字段个数 ≥ 7</a-checkbox>
      </div>

      <a-alert
        v-if="snapshotResult && !snapshotResult.snapshotTimeUtc"
        type="info"
        show-icon
        style="margin-bottom: 16px"
        message="暂无快照数据：每日 03:10 自动采集，或点击「重新采集」立即执行"
      />

      <a-table
        :data-source="filteredItems"
        :loading="loading"
        row-key="createIndexSql"
        :pagination="{ current: page, pageSize: 20, showTotal: (t: number) => `共 ${t} 条` }"
        :scroll="{ x: 488 + tableColW + eqColW + ineqColW + incColW + MINUTE_COL_W }"
        size="small"
        @change="(p: any) => page = p.current"
      >
        <a-table-column title="序号" :width="48" align="center">
          <template #default="{ index }">{{ (page - 1) * 20 + index + 1 }}</template>
        </a-table-column>
        <a-table-column title="评分" data-index="score" :width="85" :sorter="(a: any, b: any) => a.score - b.score" default-sort-order="descend">
          <template #default="{ record }">
            <div class="numcell">
              <div class="numbar" :style="{ width: barWidth(record.score, maxScore) }"></div>
              <span>{{ record.score.toFixed(2) }}</span>
            </div>
          </template>
        </a-table-column>
        <a-table-column title="表" :width="tableColW">
          <template #default="{ record }"><EllipsisText :text="`${record.dbName}.${record.tableName}`" viewer-title="表" mono /></template>
        </a-table-column>
        <a-table-column title="等值列" :width="eqColW">
          <template #default="{ record }"><EllipsisText :text="record.equalityColumns" viewer-title="等值列" mono /></template>
        </a-table-column>
        <a-table-column title="不等值列" :width="ineqColW">
          <template #default="{ record }"><EllipsisText :text="record.inequalityColumns" viewer-title="不等值列" mono /></template>
        </a-table-column>
        <a-table-column title="包含列" :width="incColW">
          <template #default="{ record }"><EllipsisText :text="record.includedColumns" viewer-title="包含列" mono /></template>
        </a-table-column>
        <a-table-column title="查找次数" data-index="userSeeks" :width="90" :sorter="(a: any, b: any) => a.userSeeks - b.userSeeks">
          <template #default="{ record }">
            <div class="numcell">
              <div class="numbar" :style="{ width: barWidth(record.userSeeks, maxSeeks) }"></div>
              <span>{{ record.userSeeks }}</span>
            </div>
          </template>
        </a-table-column>
        <a-table-column title="提升%" data-index="avgUserImpact" :width="80" :sorter="(a: any, b: any) => a.avgUserImpact - b.avgUserImpact">
          <template #default="{ record }">
            <div class="numcell">
              <div class="numbar" :style="{ width: barWidth(record.avgUserImpact, 100) }"></div>
              <span>{{ record.avgUserImpact.toFixed(0) }}</span>
            </div>
          </template>
        </a-table-column>
        <a-table-column title="最近查找" :width="MINUTE_COL_W">
          <template #default="{ record }">{{ fmtTimeMinute(record.lastUserSeek) }}</template>
        </a-table-column>
        <a-table-column title="标注" :width="100">
          <template #default="{ record }">
            <a-tag v-if="record.isNew" color="blue">近 7 天新增</a-tag>
            <span v-else>-</span>
          </template>
        </a-table-column>
        <a-table-column title="操作" :width="85" fixed="right">
          <template #default="{ record }">
            <a-tooltip :title="record.createIndexSql">
              <a @click="copySql(record.createIndexSql)">复制脚本</a>
            </a-tooltip>
          </template>
        </a-table-column>
      </a-table>
    </a-card>
  </div>
</template>

<style scoped>
/* 总览统计五列网格 */
.overview-grid {
  display: grid;
  grid-template-columns: repeat(5, minmax(0, 1fr));
  gap: 12px;
  margin-bottom: 16px;
}

/* 数值条形（numcell/numbar）与单行省略（oneline）已收敛至 style.css 全局 */

/* 小密度表格：tag 收紧 */
:deep(.ant-table-small .ant-tag) {
  margin-inline-end: 0;
  padding: 0 5px;
  font-size: 12px;
  line-height: 18px;
}
</style>
