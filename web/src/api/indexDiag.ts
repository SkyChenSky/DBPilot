import { apiGet, apiPost, http, type ApiBody } from './http'

/** 缺失索引总览统计（对齐阿里云） */
export interface MissingIndexOverview {
  total: number
  highImpact: number
  lastDayCount: number
  lastWeekCount: number
  lastMonthCount: number
  highImpactPercent: number
  lastDayPercent: number
  lastWeekPercent: number
  lastMonthPercent: number
}

/** 索引使用率快照行（合并口径：is_unused 采集固化 + 碎片率并入快照） */
export interface IndexUsageSnapshotItem {
  dbName: string
  tableName: string
  indexName: string
  typeDesc: string
  isPrimaryKey: boolean
  isUnique: boolean
  /** 键列裸列名 "A,B"（不含 INCLUDE） */
  keyColumns?: string | null
  userSeeks: number
  userScans: number
  userLookups: number
  userUpdates: number
  lastUserSeek?: string | null
  lastUserScan?: string | null
  lastUserUpdate?: string | null
  usedPageCount?: number | null
  isUnused: boolean
  avgFragmentationPercent?: number | null
  /** 处置建议 0=无需处理 1=REORGANIZE 2=REBUILD */
  action: 0 | 1 | 2
  /** 页数 < 1000：不建议处理 */
  skipSmall: boolean
  fragScript: string
  disableScript?: string | null
}

/** 索引使用率总览统计（对齐阿里云） */
export interface IndexUsageOverview {
  total: number
  totalPages: number
  totalSpaceMb: number
  fragOver30Count: number
  lowReadCount: number
  lowReadRatioCount: number
}

export interface IndexUsageSnapshotResult {
  items: IndexUsageSnapshotItem[]
  overview: IndexUsageOverview
  instanceStartTimeUtc?: string | null
  dataIncomplete: boolean
  /** 上次采集时间；null = 尚无快照 */
  snapshotTimeUtc?: string | null
}

/** 索引空间变化趋势点（每快照批次 used_page_count 求和） */
export interface IndexUsageTrendPoint {
  snapshotTimeUtc: string
  totalPages: number
}

/** 缺失索引快照行（每日 03:10 追加的原始建议行） */
export interface MissingIndexSnapshotItem {
  dbName: string
  tableName: string
  equalityColumns?: string | null
  inequalityColumns?: string | null
  includedColumns?: string | null
  userSeeks: number
  avgTotalUserCost: number
  avgUserImpact: number
  score: number
  lastUserSeek?: string | null
  tablePages?: number | null
  tableRows?: number | null
  createIndexSql: string
  /** 近 7 天新增：组合（库+表+三组列）在 7 天前快照中不存在 */
  isNew: boolean
}

export interface MissingIndexSnapshotResult {
  items: MissingIndexSnapshotItem[]
  overview: MissingIndexOverview
  /** 上次采集时间；null = 尚无快照 */
  snapshotTimeUtc?: string | null
}

/** 缺失索引变化趋势点（按快照批次计数） */
export interface MissingIndexTrendPoint {
  snapshotTimeUtc: string
  count: number
}

/** 手动重新采集（与每日 03:10 Job 同一代码路径；后端有冷却窗口防频繁采集） */
export function recollectMissingIndexes(id: number) {
  return apiPost<boolean>(`/instances/${id}/index/missing/recollect`)
}

/** 最新缺失索引快照（每日采集） */
export function getMissingIndexSnapshots(id: number, db: string, excludeSystemDb = false) {
  return apiGet<MissingIndexSnapshotResult>(`/instances/${id}/index/missing/snapshots`, { db, excludeSystemDb })
}

/** 缺失索引变化趋势（按快照批次计数） */
export function getMissingIndexTrend(id: number, db: string, excludeSystemDb = false) {
  return apiGet<MissingIndexTrendPoint[]>(`/instances/${id}/index/missing/trend`, { db, excludeSystemDb })
}

/** 最新索引使用率快照（每日 Job + 重新采集；db 为空 = 全部库） */
export function getUsageSnapshot(id: number, db: string, excludeSystemDb = false) {
  return apiGet<IndexUsageSnapshotResult>(`/instances/${id}/index/usage/snapshots`, { db, excludeSystemDb })
}

/** 索引空间变化趋势（每快照批次页数求和） */
export function getUsageTrend(id: number, db: string, excludeSystemDb = false) {
  return apiGet<IndexUsageTrendPoint[]>(`/instances/${id}/index/usage/trend`, { db, excludeSystemDb })
}

/**
 * 索引使用率重新采集：大库逐表碎片扫描（200ms/表）分钟级完成 ——
 * 不走 apiPost（全局 30s 超时），单独放宽到 10 分钟；拦截器（冷却报错等）行为不变。
 */
export async function recollectUsage(id: number): Promise<boolean> {
  const resp = await http.post<ApiBody<boolean>>(
    `/instances/${id}/index/usage/recollect`,
    undefined,
    { timeout: 600_000 },
  )
  return resp.data.data
}
