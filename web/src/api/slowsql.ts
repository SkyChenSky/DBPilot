import { apiGet } from './http'
import type { PageData } from './instance'

/** 慢SQL明细行（列表，文本为预览） */
export interface SlowSqlItem {
  id: number
  eventTimeUtc: string
  dbName?: string | null
  loginName?: string | null
  hostName?: string | null
  appName?: string | null
  sessionId?: number | null
  /** 1=rpc 2=batch */
  sqlType: number
  durationMs: number
  cpuMs?: number | null
  logicalReads?: number | null
  physicalReads?: number | null
  writes?: number | null
  rowCount?: number | null
  fingerprint?: string | null
  sqlPreview: string
}

/** 慢SQL单条全文 */
export interface SlowSqlDetail extends SlowSqlItem {
  sqlText: string
}

/** 模板聚合行（阿里云口径：总/平均/最大 + 耗时占比） */
export interface SlowSqlTemplate {
  fingerprint: string
  sampleSql: string
  dbName?: string | null
  count: number
  /** 耗时比例（%，本指纹总耗时 / 全部慢SQL总耗时） */
  totalRatio?: number | null
  avgMs: number
  maxMs: number
  cpuTotalMs?: number | null
  cpuAvgMs?: number | null
  cpuMaxMs?: number | null
  rowsAvg?: number | null
  rowsMax?: number | null
  readsTotal?: number | null
  readsAvg?: number | null
  readsMax?: number | null
  preadsTotal?: number | null
  preadsAvg?: number | null
  preadsMax?: number | null
  writesTotal?: number | null
  writesAvg?: number | null
  writesMax?: number | null
}

/** 趋势点 */
export interface SlowSqlTrendPoint {
  timeUtc: string
  count: number
  totalMs: number
}

/** 慢SQL明细分页 */
export function getSlowSqlList(id: number, params: {
  page?: number
  limit?: number
  from?: string
  to?: string
  db?: string
  minDurationMs?: number
  fingerprint?: string
  excludeSystemDb?: boolean
}) {
  return apiGet<PageData<SlowSqlItem>>(`/instances/${id}/slow-sql`, params)
}

/** 慢SQL单条全文 */
export function getSlowSqlDetail(rowId: number) {
  return apiGet<SlowSqlDetail>(`/slow-sql/${rowId}`)
}

/** 慢SQL模板聚合 Top */
export function getSlowSqlTemplates(id: number, params: {
  from?: string
  to?: string
  db?: string
  minDurationMs?: number
  excludeSystemDb?: boolean
}) {
  return apiGet<SlowSqlTemplate[]>(`/instances/${id}/slow-sql/templates`, params)
}

/** 慢SQL量级趋势（条数/总耗时；筛选口径与明细一致） */
export function getSlowSqlTrend(id: number, params: {
  from?: string
  to?: string
  db?: string
  minDurationMs?: number
  fingerprint?: string
  excludeSystemDb?: boolean
}) {
  return apiGet<SlowSqlTrendPoint[]>(`/instances/${id}/slow-sql/stats`, params)
}
