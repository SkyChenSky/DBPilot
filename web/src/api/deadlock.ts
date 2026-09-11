import { apiGet } from './http'
import type { PageData } from './instance'

/** 死锁进程 */
export interface DeadlockProcess {
  id: string
  spid: number
  isVictim: boolean
  loginName?: string | null
  hostName?: string | null
  clientApp?: string | null
  isolationLevel?: string | null
  lockMode?: string | null
  waitResource?: string | null
  status?: string | null
  transactionName?: string | null
  currentDatabaseId: number
  trancount: number
  logUsed: number
  waitTimeMs: number
  taskPriority: number
  lastTranStartedUtc?: string | null
  lastBatchStartedUtc?: string | null
  lastBatchCompletedUtc?: string | null
  inputBuf?: string | null
  executionStack: string[]
}

/** 资源侧的进程引用 */
export interface DeadlockProcessRef {
  processId: string
  mode: string
}

/** 死锁资源（关系图资源节点） */
export interface DeadlockResource {
  id: string
  resourceType: string
  display: string
  objectName?: string | null
  indexName?: string | null
  databaseId?: string | null
  mode?: string | null
  owners: DeadlockProcessRef[]
  waiters: DeadlockProcessRef[]
}

/** 死锁列表行 */
export interface DeadlockListItem {
  id: number
  eventTimeUtc: string
  victimSpids: string
  victimSummary?: string | null
  otherSummary?: string | null
  /** 参与方进程数（含牺牲方） */
  processCount: number
  objects: string
  fingerprint: string
}

/** 死锁趋势点（分色 = 事件×类型各计一次，total = 按事件计） */
export interface DeadlockTrendPoint {
  timeUtc: string
  total: number
  keyLocks: number
  objectLocks: number
  pageLocks: number
  ridLocks: number
  otherLocks: number
}

/** 相似死锁归并统计（同指纹 = 同构死锁反复发生） */
export interface DeadlockFingerprintStat {
  fingerprint: string
  count: number
  firstTimeUtc: string
  lastTimeUtc: string
  objects: string
  victimSummary?: string | null
}

/** 死锁详情 */
export interface DeadlockDetail extends DeadlockListItem {
  instanceId: number
  processes: DeadlockProcess[]
  resources: DeadlockResource[]
  graphXml: string
}

/** 死锁列表过滤条件（锁类型：keylock/objectlock/pagelock/ridlock/other）；type 别名以获得隐式索引签名 */
export type DeadlockListFilter = {
  fingerprint?: string
  lockType?: string
  objectName?: string
  loginName?: string
  hostName?: string
}

/** 死锁分页列表 */
export function getDeadlocks(id: number, params: {
  page?: number
  limit?: number
  from?: string
  to?: string
} & DeadlockListFilter) {
  return apiGet<PageData<DeadlockListItem>>(`/instances/${id}/deadlocks`, params)
}

/** 过滤下拉选项（登录名/主机名，时间窗与列表一致） */
export function getDeadlockFilters(id: number, params: { from?: string; to?: string }) {
  return apiGet<{ loginNames: string[]; hostNames: string[] }>(`/instances/${id}/deadlocks/filters`, params)
}

/** 死锁详情 */
export function getDeadlockDetail(eventId: number) {
  return apiGet<DeadlockDetail>(`/deadlocks/${eventId}`)
}

/** 死锁锁类型趋势 */
export function getDeadlockStats(id: number, params: { from?: string; to?: string }) {
  return apiGet<DeadlockTrendPoint[]>(`/instances/${id}/deadlocks/stats`, params)
}

/** 相似死锁归并 TOP */
export function getDeadlockFingerprints(id: number, params: { from?: string; to?: string }) {
  return apiGet<DeadlockFingerprintStat[]>(`/instances/${id}/deadlocks/fingerprints`, params)
}

/** 死锁速率趋势（降级形态：无事件明细的引擎读指标序列聚合，全引擎可用） */
export interface DeadlockMetricsTrend {
  bucketSeconds: number
  times: string[]
  deadlocksPerSec: (number | null)[]
}

/** 死锁速率趋势（降级形态） */
export function getDeadlockMetricsTrend(id: number, params: { from?: string; to?: string }) {
  return apiGet<DeadlockMetricsTrend>(`/instances/${id}/deadlocks/stats-metrics`, params)
}
