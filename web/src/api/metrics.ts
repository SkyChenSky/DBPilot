import { apiGet } from './http'

/**
 * 实例性能指标趋势：服务端已按桶降采样（桶内 avg，空桶 null 断点），
 * 列式序列全部共用 times 时间轴。
 */
export interface MetricsTrend {
  /** 桶粒度秒数（X 轴标签格式化参考） */
  bucketSeconds: number
  times: string[]
  cpuUsagePct: (number | null)[]
  memUsagePct: (number | null)[]
  qps: (number | null)[]
  tps: (number | null)[]
  loginsPerSec: (number | null)[]
  compilationsPerSec: (number | null)[]
  recompilationsPerSec: (number | null)[]
  fullScansPerSec: (number | null)[]
  lazyWritesPerSec: (number | null)[]
  ple: (number | null)[]
  bufferCacheHitRatioPct: (number | null)[]
  deadlocksPerSec: (number | null)[]
  lockTimeoutsPerSec: (number | null)[]
  lockWaitsPerSec: (number | null)[]
  userConnections: (number | null)[]
  blockedProcesses: (number | null)[]
  iopsRead: (number | null)[]
  iopsWrite: (number | null)[]
  mbpsRead: (number | null)[]
  mbpsWrite: (number | null)[]
}

/** 磁盘使用率趋势：每卷一条序列 */
export interface DiskVolumeSeries {
  volumeMountPoint: string
  times: string[]
  usedPct: (number | null)[]
}

export interface DiskUsageTrend {
  bucketSeconds: number
  volumes: DiskVolumeSeries[]
}

/** 指标趋势（bucketSeconds 缺省 = 按跨度自适应：≥7 天→5min、≥3 天→2min、其余→60s） */
export function getMetricsTrend(id: number, params: { from?: string; to?: string; bucketSeconds?: number }) {
  return apiGet<MetricsTrend>(`/instances/${id}/metrics/trend`, params)
}

/** 磁盘使用率趋势（每卷一条序列） */
export function getMetricsDisk(id: number, params: { from?: string; to?: string }) {
  return apiGet<DiskUsageTrend>(`/instances/${id}/metrics/disk`, params)
}
