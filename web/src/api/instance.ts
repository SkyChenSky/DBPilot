import { apiGet, apiPost, apiPut, apiDelete } from './http'

/** 实例列表项（不含凭据） */
export interface InstanceItem {
  id: number
  name: string
  host: string
  port: number
  /** 引擎标识：sqlserver / mysql / postgresql */
  engine: string
  loginName: string
  enabled: boolean
  /** 0未知 1在线 2退避 3离线 */
  status: number
  serverVersion?: string | null
  majorVersion?: number | null
  edition?: string | null
  cpuCores?: number | null
  machineName?: string | null
  envTag?: string | null
  slowSqlThresholdMs?: number | null
  blockingThresholdSec?: number | null
  commandTimeoutSeconds?: number | null
  xeFilePath?: string | null
  lastError?: string | null
  lastHeartbeat?: string | null
  createTime: string
  updateTime?: string | null
}

/** 分页响应（PageList<T>） */
export interface PageData<T> {
  total: number
  items: T[]
  pageIndex: number
  pageSize: number
  totalPage: number
  hasPrev: boolean
  hasNext: boolean
}

/** 创建/更新实例请求（更新时 password 为空 = 不修改；engine 为空 = 保持不变） */
export interface InstanceSaveRequest {
  name: string
  host: string
  port?: number
  engine?: string
  loginName: string
  password?: string
  enabled: boolean
  envTag?: string
  slowSqlThresholdMs?: number
  blockingThresholdSec?: number
  commandTimeoutSeconds?: number
  xeFilePath?: string
  dbFilter?: string
}

/** 引擎元数据（表单下拉 + 默认端口联动 + 列表徽标色） */
export const ENGINE_META: { value: string; label: string; port: number; badgeColor: string }[] = [
  { value: 'sqlserver', label: 'SQL Server', port: 1433, badgeColor: 'geekblue' },
  { value: 'mysql', label: 'MySQL', port: 3306, badgeColor: 'volcano' },
  { value: 'postgresql', label: 'PostgreSQL', port: 5432, badgeColor: 'green' },
]

/** 引擎标识 → 展示名（未知原样返回） */
export function engineLabel(engine?: string | null): string {
  return ENGINE_META.find(e => e.value === engine)?.label ?? engine ?? '-'
}

/** 引擎标识 → 徽标色（未知回落 SQL Server 蓝） */
export function engineBadgeColor(engine?: string | null): string {
  return ENGINE_META.find(e => e.value === engine)?.badgeColor ?? 'geekblue'
}

/** 各引擎默认端口（引擎切换时端口联动查表） */
export const ENGINE_PORTS: Record<string, number> = Object.fromEntries(ENGINE_META.map(e => [e.value, e.port]))

/** 缺失权限 */
export interface MissingPermission {
  permission: string
  impact: string
  fixScript: string
}

/** 连接测试结果 */
export interface ConnectionTestResult {
  ok: boolean
  error?: string | null
  latencyMs: number
  missingPermissions: MissingPermission[]
}

export function listInstances(params: { page?: number; limit?: number; keyword?: string }) {
  return apiGet<PageData<InstanceItem>>('/instances', params)
}

export function createInstance(body: InstanceSaveRequest) {
  return apiPost<InstanceItem>('/instances', body)
}

export function updateInstance(id: number, body: InstanceSaveRequest) {
  return apiPut<InstanceItem>(`/instances/${id}`, body)
}

export function deleteInstance(id: number) {
  return apiDelete<null>(`/instances/${id}`)
}

/** 测试未保存凭据（接入向导） */
export function testUnsavedInstance(body: InstanceSaveRequest) {
  return apiPost<ConnectionTestResult>('/instances/test', body)
}

/** 测试已保存实例 */
export function testInstance(id: number) {
  return apiPost<ConnectionTestResult>(`/instances/${id}/test`)
}

export function listDatabases(id: number) {
  return apiGet<string[]>(`/instances/${id}/databases`)
}

/** 版本号 → 展示名（SQL Server：10=2008, 105=2008R2, 11=2012 …；PostgreSQL：主版本号原样） */
export function versionLabel(majorVersion?: number | null, engine?: string | null): string {
  if (majorVersion == null) return '-'
  if (engine === 'postgresql') return String(majorVersion)
  const map: Record<number, string> = {
    10: '2008',
    105: '2008 R2',
    11: '2012',
    12: '2014',
    13: '2016',
    14: '2017',
    15: '2019',
    16: '2022',
    17: '2025',
  }
  return map[majorVersion] ?? (majorVersion >= 180 ? `${majorVersion / 10}` : `${majorVersion}`)
}
