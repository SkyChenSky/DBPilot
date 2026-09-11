import { apiDelete, apiGet, apiPost } from './http'

/** 实时 Top SQL：实例启动以来累计值，轮询 10s */
export interface TopSqlRealtimeItem {
  fingerprint: string
  dbName?: string | null
  sqlText: string
  /** 完整批处理文本（与 Statement 对照展示） */
  fullSqlText: string
  executionCount: number
  totalElapsedMs: number
  avgElapsedMs: number
  totalCpuMs: number
  avgCpuMs: number
  totalLogicalReads: number
  totalPhysicalReads: number
  lastExecutionTimeUtc?: string | null
  /** 占展示的 TopN 行合计的百分比（TopN 变化占比随之变化，之和 = 100%） */
  executionCountPercent: number
  totalElapsedPercent: number
  totalCpuPercent: number
  logicalReadsPercent: number
}

export interface TopSqlRealtimeResult {
  metric: 'avg' | 'total'
  topN: number
  items: TopSqlRealtimeItem[]
  snapshotTime: string
  instanceStartTimeUtc?: string | null
  /** 实例运行 < 30 天：累计值可能不代表近期负载 */
  dataIncomplete: boolean
}

export function getTopSqlRealtime(id: number, params: { db: string; metric: 'avg' | 'total'; topN: number; excludeSystemDb?: boolean }) {
  return apiGet<TopSqlRealtimeResult>(`/instances/${id}/top-sql/realtime`, params)
}

/** 历史 Top SQL：分钟差值按（指纹, 库）窗口聚合 */
export interface TopSqlHistoryItem {
  fingerprint: string
  dbName?: string | null
  /** 模板可能未采集（该指纹首条差值早于模板落库），调用方须防空 */
  sqlText?: string | null
  executionCount: number
  totalElapsedMs: number
  avgElapsedMs: number
  totalCpuMs: number
  avgCpuMs: number
  totalLogicalReads: number
  totalPhysicalReads: number
  totalWrites: number
  /** 窗口内单分钟差值的最大耗时 */
  maxElapsedMs: number
  firstSeenUtc: string
  lastSeenUtc: string
  executionCountPercent: number
  totalElapsedPercent: number
  totalCpuPercent: number
  logicalReadsPercent: number
}

export interface TopSqlHistoryResult {
  metric: TopSqlHistoryMetric
  topN: number
  items: TopSqlHistoryItem[]
}

export type TopSqlHistoryMetric = 'total' | 'avg' | 'count' | 'cpu' | 'reads'

export function getTopSqlHistory(id: number, params: { db: string; metric: TopSqlHistoryMetric; topN: number; excludeSystemDb?: boolean }) {
  return apiGet<TopSqlHistoryResult>(`/instances/${id}/top-sql/history`, params)
}

/** 指纹黑名单项（平台库 dbpilot_top_sql_exclusion，跨实例全局生效） */
export interface TopSqlExclusion {
  id: number
  fingerprint: string
  sqlHead?: string | null
  createdAt: string
}

/** 黑名单列表（已排除项管理弹窗） */
export function getTopSqlExclusions() {
  return apiGet<TopSqlExclusion[]>('/top-sql/exclusions')
}

/** 加入黑名单（fingerprint = query_hash hex，sqlHead 为备注片段） */
export function addTopSqlExclusion(fingerprint: string, sqlHead?: string) {
  return apiPost<boolean>('/top-sql/exclusions', { fingerprint, sqlHead })
}

/** 移出黑名单（恢复显示） */
export function removeTopSqlExclusion(id: number) {
  return apiDelete<boolean>(`/top-sql/exclusions/${id}`)
}

// ---------------- 执行计划分析 ----------------

/** 计划版本行（SQL 行"计划"弹窗：该指纹下全部计划快照） */
export interface PlanVersionItem {
  planId: number
  queryPlanHash: string
  compileTimeUtc?: string | null
  firstSeenUtc: string
  lastSeenUtc: string
  executionCount: number
  avgElapsedMs?: number | null
  avgWorkerMs?: number | null
  avgReads?: number | null
  /** XML 是否留存（驱逐/超 1MB 未留存，树与原文不可看） */
  hasXml: boolean
}

/** 计划变更事件（前后指标为检测时固化的累计均值） */
export interface PlanChangeItem {
  id: number
  oldPlanHash?: string | null
  newPlanHash: string
  oldAvgElapsedMs?: number | null
  newAvgElapsedMs?: number | null
  oldAvgWorkerMs?: number | null
  newAvgWorkerMs?: number | null
  oldAvgReads?: number | null
  newAvgReads?: number | null
  oldExecCount?: number | null
  newExecCount?: number | null
  changedAtUtc: string
}

/** 计划弹窗聚合（版本列表 + 变更时间线） */
export interface PlanVersionsResult {
  fingerprint: string
  dbName?: string | null
  items: PlanVersionItem[]
  changes: PlanChangeItem[]
}

/** 计划变更榜行（Top SQL 页"计划变更"tab：倍数 >1 变慢、<1 变快） */
export interface PlanChangeBoardItem extends PlanChangeItem {
  instanceId: number
  fingerprint: string
  dbName?: string | null
  sqlText?: string | null
  elapsedRatio?: number | null
  workerRatio?: number | null
  readsRatio?: number | null
}

/** 计划树节点（后端 RelOp 递归结构） */
export interface PlanTreeNode {
  physicalOp: string
  logicalOp: string
  subtreeCost: number
  estimateRows: number
  estimateExecutions: number
  objectName?: string | null
  predicate?: string | null
  seekPredicates?: string | null
  parallel: boolean
  spillWarning: boolean
  children: PlanTreeNode[]
}

/** 计划版本与变更（弹窗用） */
export function getPlans(id: number, params: { fingerprint: string; db?: string }) {
  return apiGet<PlanVersionsResult>(`/instances/${id}/top-sql/plans`, params)
}

/** 计划树（XML 缺失返回空数组） */
export function getPlanTree(id: number, planId: number) {
  return apiGet<PlanTreeNode[]>(`/instances/${id}/top-sql/plan-tree/${planId}`)
}

/** 计划 XML 原文（驱逐未留存返回 null） */
export function getPlanXml(id: number, planId: number) {
  return apiGet<string | null>(`/instances/${id}/top-sql/plan-xml/${planId}`)
}

/** 最近计划变更榜 */
export function getPlanChanges(id: number, params?: { topN?: number }) {
  return apiGet<PlanChangeBoardItem[]>(`/instances/${id}/top-sql/plan-changes`, params)
}
