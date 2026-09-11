import { apiGet } from './http'

/** 桶 → 中文名（tooltip；图例直接用桶 key，本身即英文） */
export type BucketNames = Record<string, string>

/** 单个 AAS 点（实时 10s / 历史 1min 粒度共用） */
export interface AasPoint {
  timeUtc: string
  active: number
  buckets: Record<string, number>
  /** 维度 → 维度值 → 贡献（AAS 分类切换用） */
  dims?: Record<string, Record<string, number>>
}

/** 实时 AAS（内存环形缓冲直出） */
export interface AasRealtimeResult {
  cpuCores?: number | null
  bucketNames: BucketNames
  lastTickUtc?: string | null
  points: AasPoint[]
}

export interface AasHistoryPoint extends AasPoint {
  maxActive: number
  sampleCount: number
}

/** 历史 AAS（分钟聚合表） */
export interface AasHistoryResult {
  cpuCores?: number | null
  bucketNames: BucketNames
  points: AasHistoryPoint[]
}

/** Load By SQL 行 */
export interface TopSqlItem {
  fingerprint: string
  aas: number
  /** 占该维度总 AAS 的百分比（0~100，1 位小数） */
  percent: number
  /** AAS 构成：等待桶 → 区间均值贡献 */
  buckets?: Record<string, number>
  sqlText?: string | null
}

/** 单条 SQL 的 AAS 趋势 */
export interface SqlTrendResult {
  fingerprint: string
  sqlText?: string | null
  granularity: string
  points: { timeUtc: string; value?: number | null }[]
}

/** 实时 AAS（minutes ≤ 60，默认 15；页面轮询 10s） */
export function getAasRealtime(id: number, minutes = 15) {
  return apiGet<AasRealtimeResult>(`/instances/${id}/insights/aas/realtime`, { minutes })
}

/** 历史 AAS（start/end 为 UTC ISO；默认近 24h） */
export function getAasHistory(id: number, start?: string, end?: string) {
  return apiGet<AasHistoryResult>(`/instances/${id}/insights/aas`, { start, end })
}

/** Load By SQL（区间 Top10 AAS 贡献 + 语句文本） */
export function getTopSql(id: number, start?: string, end?: string) {
  return apiGet<TopSqlItem[]>(`/instances/${id}/insights/aas/top-sql`, { start, end })
}

/** 单条 SQL 的 AAS 趋势 */
export function getSqlTrend(id: number, fingerprint: string, start?: string, end?: string) {
  return apiGet<SqlTrendResult>(`/instances/${id}/insights/aas/sql-trend`, { fingerprint, start, end })
}
