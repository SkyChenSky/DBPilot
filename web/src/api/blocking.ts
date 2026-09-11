import { apiGet } from './http'
import type { PageData } from './instance'

/** 阻塞树节点 */
export interface BlockingNode {
  sessionId: number
  /** -2 孤儿分布式事务 / -3 延迟恢复（系统节点，不可 Kill） */
  isSystem: boolean
  /** 头阻塞者"睡着拿锁"（无活动请求但持有事务锁） */
  isSleepingHead: boolean
  loginName?: string | null
  hostName?: string | null
  programName?: string | null
  dbName?: string | null
  status?: string | null
  command?: string | null
  waitType?: string | null
  waitTimeMs: number
  waitResource?: string | null
  totalElapsedMs?: number | null
  openTranCount: number
  sqlText?: string | null
  /** 完整批处理文本（含批内已执行过的语句） */
  batchSqlText?: string | null
  /** 阻塞原因：锁资源（WAIT = 正在等的锁在前，GRANT = 已持有在后） */
  locks?: {
    resourceType: string
    dbName?: string | null
    objectName?: string | null
    lockMode: string
    lockStatus: string
  }[]
  depth: number
  children: BlockingNode[]
}

/** 当前阻塞总览 */
export interface BlockingOverview {
  chainCount: number
  maxWaitSeconds: number
  involvedSessions: number
  snapshotTimeUtc: string
  trees: BlockingNode[]
}

/** 当前阻塞树（页面轮询 5s） */
export function getBlockingCurrent(id: number) {
  return apiGet<BlockingOverview>(`/instances/${id}/blocking/current`)
}

/** 历史阻塞事件列表行 */
export interface BlockingEventItem {
  id: number
  headSessionId: number
  startTimeUtc: string
  endTimeUtc?: string | null
  blockedCount: number
  maxWaitSeconds: number
  /** login@host（program），留痕时定格 */
  headInfo?: string | null
  /** 头阻塞者所在库（留痕时定格；老留痕为 null） */
  headDbName?: string | null
  /** 头阻塞者 SQL 预览（chain_tree 根节点） */
  headSql?: string | null
  resolved: boolean
}

/** 历史阻塞事件详情：元信息 + 最近一次链路快照 */
export interface BlockingEventDetail extends BlockingEventItem {
  instanceId: number
  tree?: BlockingNode | null
}

/** 历史阻塞事件分页（时间按开始时间过滤，resolved 省略 = 全部，dbName 省略 = 全部库） */
export function getBlockingEvents(id: number, params: {
  page?: number
  limit?: number
  from?: string
  to?: string
  resolved?: boolean
  dbName?: string
}) {
  return apiGet<PageData<BlockingEventItem>>(`/instances/${id}/blocking/events`, params)
}

/** 阻塞数量统计（近一天 / 近一周 / 近两周，按开始时间口径） */
export interface BlockingStats {
  dayCount: number
  weekCount: number
  twoWeekCount: number
}

export function getBlockingStats(id: number) {
  return apiGet<BlockingStats>(`/instances/${id}/blocking/stats`)
}

/** 阻塞趋势时间桶 */
export interface BlockingTrendItem {
  bucketStartUtc: string
  count: number
  totalWaitSeconds: number
}

export interface BlockingTrend {
  /** 桶粒度分钟数（X 轴标签格式化参考） */
  stepMinutes: number
  items: BlockingTrendItem[]
}

export function getBlockingTrend(id: number, params: { from?: string; to?: string }) {
  return apiGet<BlockingTrend>(`/instances/${id}/blocking/trend`, params)
}

/** 历史阻塞事件详情 */
export function getBlockingEventDetail(eventId: number) {
  return apiGet<BlockingEventDetail>(`/blocking/events/${eventId}`)
}
