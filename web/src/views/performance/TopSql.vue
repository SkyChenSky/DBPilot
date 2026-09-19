<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { message } from 'ant-design-vue'
import {
  addTopSqlExclusion,
  getPlanChanges,
  getPlanTree,
  getPlanXml,
  getPlans,
  getTopSqlExclusions,
  getTopSqlHistory,
  removeTopSqlExclusion,
  type PlanChangeBoardItem,
  type PlanTreeNode as PlanTreeNodeModel,
  type PlanVersionsResult,
  type TopSqlExclusion,
  type TopSqlHistoryItem,
  type TopSqlHistoryMetric,
  type TopSqlHistoryResult,
} from '../../api/topSql'
import EllipsisText from '../../components/EllipsisText.vue'
import PlanTreeNode from '../../components/PlanTreeNode.vue'
import SqlDetailModal from '../../components/SqlDetailModal.vue'
import SqlText from '../../components/SqlText.vue'
import { useInstanceDatabases } from '../../composables/useInstanceDatabases'
import { fitColumnWidth, TIME_COL_W } from '../../utils/fitColumnWidth'
import { fmtMs, fmtNum, fmtPct, fmtTime } from '../../utils/format'
import { CAP, useEngineCaps } from '../../api/engine'

// 实例来自顶栏全局上下文：首载/切换 → 重拉库列表 + 重查总榜（计划变更榜随实例清空重查）
const { instanceId, dbOptions, dbName, dbLoading, init } = useInstanceDatabases(async () => {
  await fetch()
  changes.value = []
})

// 引擎守卫（能力矩阵）：计划快照仅有计划缓存数据源的引擎——隐藏「计划变更」tab 与「计划」入口
const { cap } = useEngineCaps()
const noPlan = computed(() => cap(CAP.queryPlanSnapshot) === 'none')

/** 历史页（总榜）：metric 是排序口径（总耗时/平均耗时/执行次数/CPU/逻辑读），默认总耗时。
 *  对齐阿里云：无时间筛选，保留期内全部差值聚合成单一总榜。 */
const metric = ref<TopSqlHistoryMetric>('total')
const topN = ref(10)
const excludeSystemDb = ref(false)
const result = ref<TopSqlHistoryResult>()
const loading = ref(false)

async function fetch() {
  if (instanceId.value === undefined) return
  loading.value = true
  try {
    result.value = await getTopSqlHistory(instanceId.value, {
      db: dbName.value ?? '',
      metric: metric.value,
      topN: topN.value,
      excludeSystemDb: excludeSystemDb.value,
    })
  } finally {
    loading.value = false
  }
}

// ---------- 执行计划分析 ----------
/** 外层页签：SQL 总榜 / 计划变更 */
const activeTab = ref('rank')
const changes = ref<PlanChangeBoardItem[]>([])
const changesLoading = ref(false)

async function fetchChanges() {
  if (noPlan.value || instanceId.value === undefined) return
  changesLoading.value = true
  try {
    changes.value = await getPlanChanges(instanceId.value, { topN: 100 })
  } finally {
    changesLoading.value = false
  }
}

// 切到无计划快照的引擎：停在「计划变更」tab 或计划弹窗开着时重置（防残留，切回支持引擎由 tab 回调重查）
watch(noPlan, (v) => {
  if (!v) return
  activeTab.value = 'rank'
  planOpen.value = false
  planResult.value = undefined
  planTree.value = []
  planXml.value = null
})

function onTabChange(key: string | number) {
  if (key === 'changes' && changes.value.length === 0 && !changesLoading.value) fetchChanges()
}

/** 倍数 tag 颜色：>1 变慢红、<1 变快绿、NULL 灰 */
function ratioColor(r?: number | null) {
  if (r == null) return 'default'
  return r > 1 ? 'red' : r < 1 ? 'green' : 'default'
}
function ratioText(r?: number | null) {
  return r == null ? '-' : `${r.toFixed(2)}x`
}
/** old_exec_count < 10 → "样本少"灰标（均值倍数参考性弱） */
function lowSample(c: PlanChangeBoardItem) {
  return c.oldExecCount != null && c.oldExecCount < 10
}

// 计划弹窗（SQL 行"计划"入口 / 变更榜"查看计划"入口）
const planOpen = ref(false)
const planLoading = ref(false)
const planResult = ref<PlanVersionsResult>()
const planFp = ref('')
const planDetailTab = ref('versions')
const selectedPlanId = ref<number>()
const planTree = ref<PlanTreeNodeModel[]>([])
const planXml = ref<string | null>(null)
const treeLoading = ref(false)

async function showPlan(fingerprint: string) {
  if (instanceId.value === undefined) return
  planFp.value = fingerprint
  planOpen.value = true
  planLoading.value = true
  planDetailTab.value = 'versions'
  planResult.value = undefined
  planTree.value = []
  planXml.value = null
  try {
    // 不按库过滤：同指纹跨库计划版本一并展示
    planResult.value = await getPlans(instanceId.value, { fingerprint })
    const first = planResult.value.items[0]
    if (first) await selectPlan(first.planId)
  } finally {
    planLoading.value = false
  }
}

/** 选中计划版本：hasXml 才拉树与原文（驱逐/超 1MB 未留存） */
async function selectPlan(planId: number) {
  selectedPlanId.value = planId
  planTree.value = []
  planXml.value = null
  if (instanceId.value === undefined) return
  const ver = planResult.value?.items.find(x => x.planId === planId)
  if (!ver?.hasXml) return
  treeLoading.value = true
  try {
    planTree.value = await getPlanTree(instanceId.value, planId)
    planXml.value = await getPlanXml(instanceId.value, planId)
  } finally {
    treeLoading.value = false
  }
}

/** 当前选中版本是否可看树/XML */
const selectedHasXml = computed(() => planResult.value?.items.find(x => x.planId === selectedPlanId.value)?.hasXml ?? false)

/** 文本计划（引擎不给 XML 时的 dm_exec_text_query_plan 兜底）：本身即缩进树形，直接等宽渲染 */
const isTextPlan = computed(() => !!planXml.value && !planXml.value.trimStart().startsWith('<'))

async function copyFingerprint(fp: string) {
  try {
    await navigator.clipboard.writeText(fp)
    message.success('指纹已复制')
  } catch {
    message.error('复制失败，请手动选择复制')
  }
}

const detailOpen = ref(false)
const detailSql = ref('')
/** 详情弹窗行：总榜行（含 executionCount）或计划变更行（含 old/new 指标） */
const detailRow = ref<TopSqlHistoryItem | PlanChangeBoardItem>()
const detailRankRow = computed(() =>
  detailRow.value != null && 'executionCount' in detailRow.value ? detailRow.value : null)
const detailChangeRow = computed(() =>
  detailRow.value != null && !('executionCount' in detailRow.value) ? detailRow.value : null)

function showDetail(row: TopSqlHistoryItem) {
  detailRow.value = row
  detailSql.value = row.sqlText || '（模板未采集：该指纹首条差值早于模板落库，可稍后重试或按指纹排查）'
  detailOpen.value = true
}

/** 计划变更榜 SQL 点击：弹全文详情（计划入口走行尾"查看计划"） */
function showChangeDetail(c: PlanChangeBoardItem) {
  detailRow.value = c
  detailSql.value = c.sqlText || `（模板未采集：指纹 ${c.fingerprint.slice(0, 16)}…）`
  detailOpen.value = true
}

// 指纹黑名单：排除操作跨实例全局生效，历史查询 NOT EXISTS 同样隐藏
const exclusions = ref<TopSqlExclusion[]>([])
const exclusionOpen = ref(false)
const exclusionLoading = ref(false)

async function loadExclusions() {
  exclusionLoading.value = true
  try {
    exclusions.value = await getTopSqlExclusions()
  } finally {
    exclusionLoading.value = false
  }
}

async function exclude(row: TopSqlHistoryItem) {
  // sqlText 可能未采集（模板早于首条差值落库）：不设防的 .slice 会同步抛错，
  // ActionButton 对 reject 静默处理 → 表现为"点排除没反应"（无请求无提示）
  await addTopSqlExclusion(row.fingerprint, (row.sqlText ?? '').slice(0, 100))
  message.success('已排除，全局生效')
  await Promise.all([fetch(), loadExclusions()])
}

async function restore(id: number) {
  await removeTopSqlExclusion(id)
  message.success('已恢复显示')
  await Promise.all([fetch(), loadExclusions()])
}

// 列宽自适应：库列按当前数据最长库名量宽
const dbColW = computed(() => fitColumnWidth((result.value?.items ?? []).map(i => i.dbName), { min: 95 }))
const changeDbColW = computed(() => fitColumnWidth(changes.value.map(c => c.dbName), { min: 90 }))

onMounted(async () => {
  loadExclusions()
  // 默认"全部库"（composable 口径）+ 首屏自动查询
  await init()
})
</script>

<template>
  <div class="page">
    <a-card>
      <!-- 工具栏（实例在顶栏全局选择，页内不再重复） -->
      <div class="toolbar" style="margin-bottom: 16px">
        <span>数据库：</span>
        <a-select
          v-model:value="dbName"
          style="width: 200px"
          placeholder="选择数据库"
          :loading="dbLoading"
          :options="dbOptions"
          @change="fetch"
        />
        <a-tooltip title="全部库视图下额外排除 master/model/msdb/tempdb 的 SQL（选具体库时无效）">
          <a-switch
            v-model:checked="excludeSystemDb"
            checked-children="排除系统库"
            un-checked-children="含系统库"
            :disabled="!!dbName"
            @change="fetch"
          />
        </a-tooltip>
        <span>排序：</span>
        <a-select
          v-model:value="metric"
          style="width: 130px"
          :options="[
            { value: 'total', label: '按总耗时' },
            { value: 'avg', label: '按平均耗时' },
            { value: 'count', label: '按执行次数' },
            { value: 'cpu', label: '按 CPU' },
            { value: 'reads', label: '按逻辑读' },
          ]"
          @change="fetch"
        />
        <span>TopN：</span>
        <a-select v-model:value="topN" style="width: 80px" :options="[10, 20, 50].map(n => ({ value: n, label: n }))" @change="fetch" />
        <a-button type="primary" :loading="loading" @click="fetch">刷新</a-button>
        <a @click="exclusionOpen = true">已排除 {{ exclusions.length }}</a>
      </div>

      <a-tabs v-model:active-key="activeTab" @change="onTabChange">
        <a-tab-pane key="rank" tab="SQL 总榜">
      <a-alert
        v-if="result && result.items.length === 0"
        type="info"
        show-icon
        style="margin-bottom: 16px"
        message="暂无差值数据：采集每分钟运行，且实例重启/平台重启后的首个周期只建基线不落库；数据保留期由 Housekeeping TopSqlDeltaDays 控制"
      />

      <a-table
        :data-source="result?.items ?? []"
        :row-key="(r: any) => `${r.dbName ?? ''}.${r.fingerprint}`"
        :pagination="false"
        :scroll="{ x: 1463 + dbColW + TIME_COL_W * 2 }"
        size="small"
      >
        <a-table-column title="序号" :width="48" align="center" fixed="left">
          <template #default="{ index }">{{ index + 1 }}</template>
        </a-table-column>
        <a-table-column title="SQL 语句" :width="380" fixed="left">
          <template #default="{ record }">
            <a style="word-break: break-all" @click="showDetail(record)">{{ (record.sqlText || `（指纹 ${record.fingerprint.slice(0, 16)}…）`).slice(0, 120) }}</a>
          </template>
        </a-table-column>
        <a-table-column title="库" :width="dbColW" ellipsis>
          <template #default="{ record }">{{ record.dbName ?? '-' }}</template>
        </a-table-column>
        <a-table-column title="执行次数" data-index="executionCount" :width="100" :sorter="(a: any, b: any) => a.executionCount - b.executionCount">
          <template #default="{ record }">
            {{ fmtNum(record.executionCount) }}
            <span class="text-tertiary">{{ fmtPct(record.executionCountPercent) }}</span>
          </template>
        </a-table-column>
        <a-table-column title="总耗时" data-index="totalElapsedMs" :width="110" :sorter="(a: any, b: any) => a.totalElapsedMs - b.totalElapsedMs">
          <template #default="{ record }">
            <span :style="metric === 'total' ? 'font-weight: 600' : ''">{{ fmtMs(record.totalElapsedMs) }}</span>
            <span class="text-tertiary">{{ fmtPct(record.totalElapsedPercent) }}</span>
          </template>
        </a-table-column>
        <a-table-column title="平均耗时" data-index="avgElapsedMs" :width="95" :sorter="(a: any, b: any) => a.avgElapsedMs - b.avgElapsedMs">
          <template #default="{ text }">
            <span :style="metric === 'avg' ? 'font-weight: 600' : ''">{{ fmtMs(text) }}</span>
          </template>
        </a-table-column>
        <a-table-column title="最大耗时" data-index="maxElapsedMs" :width="95" :sorter="(a: any, b: any) => a.maxElapsedMs - b.maxElapsedMs">
          <template #default="{ text }">{{ fmtMs(text) }}</template>
        </a-table-column>
        <a-table-column title="总 CPU" data-index="totalCpuMs" :width="100" :sorter="(a: any, b: any) => a.totalCpuMs - b.totalCpuMs">
          <template #default="{ record }">
            {{ fmtMs(record.totalCpuMs) }}
            <span class="text-tertiary">{{ fmtPct(record.totalCpuPercent) }}</span>
          </template>
        </a-table-column>
        <a-table-column title="平均 CPU" data-index="avgCpuMs" :width="95" :sorter="(a: any, b: any) => a.avgCpuMs - b.avgCpuMs">
          <template #default="{ text }">{{ fmtMs(text) }}</template>
        </a-table-column>
        <a-table-column title="逻辑读" data-index="totalLogicalReads" :width="115" :sorter="(a: any, b: any) => a.totalLogicalReads - b.totalLogicalReads">
          <template #default="{ record }">
            {{ fmtNum(record.totalLogicalReads) }}
            <span class="text-tertiary">{{ fmtPct(record.logicalReadsPercent) }}</span>
          </template>
        </a-table-column>
        <a-table-column title="物理读" data-index="totalPhysicalReads" :width="90" :sorter="(a: any, b: any) => a.totalPhysicalReads - b.totalPhysicalReads">
          <template #default="{ text }">{{ fmtNum(text) }}</template>
        </a-table-column>
        <a-table-column title="总写入" data-index="totalWrites" :width="90" :sorter="(a: any, b: any) => a.totalWrites - b.totalWrites">
          <template #default="{ text }">{{ fmtNum(text) }}</template>
        </a-table-column>
        <a-table-column title="首次出现" :width="TIME_COL_W">
          <template #default="{ record }">{{ fmtTime(record.firstSeenUtc) }}</template>
        </a-table-column>
        <a-table-column title="最近执行" :width="TIME_COL_W">
          <template #default="{ record }">{{ fmtTime(record.lastSeenUtc) }}</template>
        </a-table-column>
        <a-table-column title="操作" :width="145" fixed="right">
          <template #default="{ record }">
            <a-space>
              <a-tooltip :title="record.fingerprint">
                <a @click="copyFingerprint(record.fingerprint)">指纹</a>
              </a-tooltip>
              <a v-if="!noPlan" @click="showPlan(record.fingerprint)">计划</a>
              <a-popconfirm
                title="排除该 SQL？（跨实例全局生效）"
                ok-text="排除"
                @confirm="exclude(record)"
              >
                <a class="danger-link">排除</a>
              </a-popconfirm>
            </a-space>
          </template>
        </a-table-column>
      </a-table>
        </a-tab-pane>

        <a-tab-pane v-if="!noPlan" key="changes" tab="计划变更">
          <a-alert
            v-if="!changesLoading && changes.length === 0"
            type="info"
            show-icon
            style="margin-bottom: 16px"
            message="暂无计划变更事件：采集每 5 分钟运行；部署首拍全部为种子（不产事件），之后同指纹出现新计划（plan_hash 变化）才会记录"
          />
          <a-table
            :data-source="changes"
            :row-key="(r: any) => r.id"
            :loading="changesLoading"
            :pagination="false"
            :scroll="{ x: 1240 + changeDbColW + TIME_COL_W }"
            size="small"
          >
            <a-table-column title="变更时间" :width="TIME_COL_W" fixed="left">
              <template #default="{ record }">{{ fmtTime(record.changedAtUtc) }}</template>
            </a-table-column>
            <a-table-column title="SQL 语句" :width="360">
              <template #default="{ record }">
                <a @click="showChangeDetail(record)">{{ (record.sqlText || `（指纹 ${record.fingerprint.slice(0, 16)}…）`).slice(0, 100) }}</a>
              </template>
            </a-table-column>
            <a-table-column title="库" :width="changeDbColW" ellipsis>
              <template #default="{ record }">{{ record.dbName ?? '-' }}</template>
            </a-table-column>
            <a-table-column title="平均耗时（旧 → 新）" :width="180">
              <template #default="{ record }">{{ fmtMs(record.oldAvgElapsedMs) }} → {{ fmtMs(record.newAvgElapsedMs) }}</template>
            </a-table-column>
            <a-table-column title="倍数" :width="130">
              <template #default="{ record }">
                <a-space :size="4">
                  <a-tag :color="ratioColor(record.elapsedRatio)">{{ ratioText(record.elapsedRatio) }}</a-tag>
                  <a-tag v-if="lowSample(record)" color="default">样本少</a-tag>
                </a-space>
              </template>
            </a-table-column>
            <a-table-column title="平均 CPU（旧 → 新）" :width="150">
              <template #default="{ record }">{{ fmtMs(record.oldAvgWorkerMs) }} → {{ fmtMs(record.newAvgWorkerMs) }}</template>
            </a-table-column>
            <a-table-column title="平均逻辑读（旧 → 新）" :width="160">
              <template #default="{ record }">{{ record.oldAvgReads == null ? '-' : fmtNum(record.oldAvgReads) }} → {{ record.newAvgReads == null ? '-' : fmtNum(record.newAvgReads) }}</template>
            </a-table-column>
            <a-table-column title="计划哈希（旧 → 新）" :width="170">
              <template #default="{ record }">
                <span style="font-family: consolas, monospace; font-size: 12px">{{ (record.oldPlanHash ?? '').slice(0, 8) }} → {{ record.newPlanHash.slice(0, 8) }}</span>
              </template>
            </a-table-column>
            <a-table-column title="操作" :width="90" fixed="right">
              <template #default="{ record }">
                <a @click="showPlan(record.fingerprint)">查看计划</a>
              </template>
            </a-table-column>
          </a-table>
        </a-tab-pane>
      </a-tabs>

      <!-- SQL 详情弹窗 -->
      <SqlDetailModal
        v-model:open="detailOpen"
        :title="detailRow ? `指纹 ${detailRow.fingerprint.slice(0, 16)}…` : ''"
        :sql="detailSql"
      >
        <template #meta>
          <template v-if="detailChangeRow">
            {{ detailChangeRow.dbName ?? '-' }} ｜ 变更 {{ fmtTime(detailChangeRow.changedAtUtc) }} ｜ 平均耗时 {{ fmtMs(detailChangeRow.oldAvgElapsedMs) }} → {{ fmtMs(detailChangeRow.newAvgElapsedMs) }} ｜ 执行次数 {{ detailChangeRow.oldExecCount ?? '-' }} → {{ detailChangeRow.newExecCount ?? '-' }}
          </template>
          <template v-else-if="detailRankRow">
            {{ detailRankRow.dbName ?? '-' }} ｜ 执行 {{ fmtNum(detailRankRow.executionCount) }} 次 ｜ 总耗时 {{ fmtMs(detailRankRow.totalElapsedMs) }} ｜ 平均 {{ fmtMs(detailRankRow.avgElapsedMs) }} ｜ 最大 {{ fmtMs(detailRankRow.maxElapsedMs) }} ｜ 逻辑读 {{ fmtNum(detailRankRow.totalLogicalReads) }}
          </template>
        </template>
      </SqlDetailModal>

      <!-- 指纹黑名单管理弹窗 -->
      <a-modal v-model:open="exclusionOpen" title="已排除的 SQL（指纹黑名单，全局生效）" :width="720" :footer="null">
        <a-table
          :data-source="exclusions"
          :row-key="(r: any) => r.id"
          :loading="exclusionLoading"
          :pagination="false"
          :scroll="{ x: 640 }"
          size="small"
        >
          <a-table-column title="指纹" :width="150">
            <template #default="{ record }"><EllipsisText :text="record.fingerprint" viewer-title="指纹" mono /></template>
          </a-table-column>
          <a-table-column title="SQL 片段">
            <template #default="{ record }"><EllipsisText :text="record.sqlHead" viewer-title="SQL 片段" /></template>
          </a-table-column>
          <a-table-column title="排除时间" :width="TIME_COL_W">
            <template #default="{ record }">{{ fmtTime(record.createdAt) }}</template>
          </a-table-column>
          <a-table-column title="操作" :width="80">
            <template #default="{ record }">
              <a-popconfirm title="恢复显示该 SQL？" ok-text="恢复" @confirm="restore(record.id)">
                <a>恢复</a>
              </a-popconfirm>
            </template>
          </a-table-column>
        </a-table>
      </a-modal>

      <!-- 执行计划弹窗（版本与变更 / 计划树 / XML 原文） -->
      <a-modal
        v-model:open="planOpen"
        :title="`执行计划 ｜ 指纹 ${planFp.slice(0, 16)}…`"
        :width="1000"
        :footer="null"
      >
        <a-spin :spinning="planLoading || treeLoading">
          <a-tabs v-model:active-key="planDetailTab">
            <a-tab-pane key="versions" tab="计划版本与变更">
              <template v-if="planResult">
                <div v-if="planResult.changes.length === 0" class="text-tertiary" style="margin-bottom: 8px">
                  暂无变更事件（首拍种子期或计划未变化）
                </div>
                <a-table
                  v-if="planResult.changes.length > 0"
                  :data-source="planResult.changes"
                  :row-key="(r: any) => r.id"
                  :pagination="false"
                  size="small"
                  style="margin-bottom: 16px"
                >
                  <a-table-column title="变更时间" :width="TIME_COL_W">
                    <template #default="{ record }">{{ fmtTime(record.changedAtUtc) }}</template>
                  </a-table-column>
                  <a-table-column title="计划哈希（旧 → 新）" :width="150">
                    <template #default="{ record }">
                      <span style="font-family: consolas, monospace; font-size: 12px">{{ (record.oldPlanHash ?? '').slice(0, 8) }} → {{ record.newPlanHash.slice(0, 8) }}</span>
                    </template>
                  </a-table-column>
                  <a-table-column title="平均耗时" :width="120">
                    <template #default="{ record }">{{ fmtMs(record.oldAvgElapsedMs) }} → {{ fmtMs(record.newAvgElapsedMs) }}</template>
                  </a-table-column>
                  <a-table-column title="平均 CPU" :width="120">
                    <template #default="{ record }">{{ fmtMs(record.oldAvgWorkerMs) }} → {{ fmtMs(record.newAvgWorkerMs) }}</template>
                  </a-table-column>
                  <a-table-column title="平均逻辑读" :width="130">
                    <template #default="{ record }">{{ record.oldAvgReads == null ? '-' : fmtNum(record.oldAvgReads) }} → {{ record.newAvgReads == null ? '-' : fmtNum(record.newAvgReads) }}</template>
                  </a-table-column>
                  <a-table-column title="执行次数" :width="120">
                    <template #default="{ record }">{{ record.oldExecCount ?? '-' }} → {{ record.newExecCount ?? '-' }}</template>
                  </a-table-column>
                </a-table>

                <div style="font-weight: 600; margin-bottom: 8px">计划版本（点击行查看计划树 / XML）</div>
                <a-table
                  :data-source="planResult.items"
                  :row-key="(r: any) => r.planId"
                  :pagination="false"
                  size="small"
                  :custom-row="(r: any) => ({ onClick: () => { planDetailTab = 'tree'; selectPlan(r.planId) } })"
                  :row-class-name="(r: any) => (r.planId === selectedPlanId ? 'ant-table-row-selected' : '')"
                >
                  <a-table-column title="编译时间" :width="TIME_COL_W">
                    <template #default="{ record }">{{ fmtTime(record.compileTimeUtc) }}</template>
                  </a-table-column>
                  <a-table-column title="计划哈希" :width="120">
                    <template #default="{ record }">
                      <span style="font-family: consolas, monospace; font-size: 12px">{{ record.queryPlanHash.slice(0, 10) }}…</span>
                    </template>
                  </a-table-column>
                  <a-table-column title="执行次数" data-index="executionCount" :width="90">
                    <template #default="{ text }">{{ fmtNum(text) }}</template>
                  </a-table-column>
                  <a-table-column title="平均耗时" :width="90">
                    <template #default="{ record }">{{ fmtMs(record.avgElapsedMs) }}</template>
                  </a-table-column>
                  <a-table-column title="平均 CPU" :width="90">
                    <template #default="{ record }">{{ fmtMs(record.avgWorkerMs) }}</template>
                  </a-table-column>
                  <a-table-column title="平均逻辑读" :width="100">
                    <template #default="{ record }">{{ record.avgReads == null ? '-' : fmtNum(record.avgReads) }}</template>
                  </a-table-column>
                  <a-table-column title="首见" :width="TIME_COL_W">
                    <template #default="{ record }">{{ fmtTime(record.firstSeenUtc) }}</template>
                  </a-table-column>
                  <a-table-column title="最近" :width="TIME_COL_W">
                    <template #default="{ record }">{{ fmtTime(record.lastSeenUtc) }}</template>
                  </a-table-column>
                  <a-table-column title="计划" :width="70">
                    <template #default="{ record }">{{ record.hasXml ? '留存' : '-' }}</template>
                  </a-table-column>
                </a-table>
              </template>
            </a-tab-pane>

            <a-tab-pane key="tree" tab="计划树">
              <!-- 空态分开报：计划未留存（无法解析）/ 树解析为空（可切 XML 原文）；文本计划直接展示 -->
              <a-alert
                v-if="!treeLoading && !selectedHasXml"
                type="info"
                show-icon
                message="计划未留存（缓存已驱逐或超 1MB），请在版本列表选择其他版本"
              />
              <div
                v-else-if="isTextPlan"
                class="plan-text"
              ><pre>{{ planXml }}</pre></div>
              <a-alert
                v-else-if="!treeLoading && planTree.length === 0"
                type="info"
                show-icon
                message="计划树解析为空（XML 已留存，可切到「XML 原文」查看）"
              />
              <div v-else style="max-height: 480px; overflow: auto; background: var(--bg-body); padding: 8px; border-radius: 6px">
                <PlanTreeNode v-for="(t, i) in planTree" :key="i" :node="t" />
              </div>
            </a-tab-pane>

            <a-tab-pane key="xml" tab="XML 原文">
              <a-alert v-if="!selectedHasXml" type="info" show-icon message="计划未留存（缓存已驱逐或超 1MB）" />
              <SqlText v-else-if="planXml" :text="planXml" :max-height="480" />
              <a-alert v-else type="info" show-icon message="请先在版本列表选择计划版本" />
            </a-tab-pane>
          </a-tabs>
        </a-spin>
      </a-modal>
    </a-card>
  </div>
</template>

<style scoped>
/* 文本计划（无 XML 时的 text_query_plan 兜底）：等宽保留缩进，即树形展示 */
.plan-text {
  max-height: 480px;
  overflow: auto;
  background: var(--bg-body);
  padding: 8px;
  border-radius: 6px;
}

.plan-text pre {
  margin: 0;
  font-family: consolas, monospace;
  font-size: 12px;
}

/* 危险操作链接（排除 SQL） */
.danger-link {
  color: var(--danger-text);
}
</style>
