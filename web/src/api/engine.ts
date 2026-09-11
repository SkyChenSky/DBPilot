import { computed, ref } from 'vue'
import { apiGet } from './http'
import { useInstanceContext } from '../stores/instanceContext'

/** 能力级别：full=完整功能 / degraded=同页降级形态 / none=隐藏入口或空态 */
export type CapabilityLevel = 'full' | 'degraded' | 'none'

/** 引擎能力矩阵（GET /api/engines）：engine → 能力键 → 级别 */
export type EngineCaps = Record<string, Record<string, CapabilityLevel>>

/** 能力键常量（与后端 DbpilotCapabilityKeys 对齐；模板榜/趋势-only 是同页降级形态） */
export const CAP = {
  deadlockEvents: 'deadlockEvents',
  deadlockTrend: 'deadlockTrend',
  queryPlanSnapshot: 'queryPlanSnapshot',
  missingIndex: 'missingIndex',
  fragmentation: 'fragmentation',
  slowSqlEvents: 'slowSqlEvents',
  slowSqlTemplates: 'slowSqlTemplates',
  osCpuMem: 'osCpuMem',
  ple: 'ple',
  compileStats: 'compileStats',
  blockedProcesses: 'blockedProcesses',
  indexDisableScript: 'indexDisableScript',
} as const

/** 模块级缓存：全站拉一次 /api/engines（矩阵是静态注册信息，会话内不变） */
const caps = ref<EngineCaps | null>(null)
let capsPromise: Promise<EngineCaps> | null = null

function ensureCaps(): Promise<EngineCaps> {
  capsPromise ??= apiGet<EngineCaps>('/engines').then((data) => {
    caps.value = data
    return data
  }).catch((e) => {
    capsPromise = null   // 失败允许重试；期间 cap() 全量按 full 降级（等同旧引擎守卫未加载口径）
    throw e
  })
  return capsPromise
}

/**
 * 引擎能力查询：cap(key) 返回当前实例引擎在该能力键的级别。
 * - 矩阵未加载 / 引擎未知（实例未加载完）→ full（防首屏闪减）；
 * - 引擎已知但不在矩阵内（未注册引擎，后端 fail-closed）→ none。
 * 返回值是响应式的（依赖 caps + currentEngine），可在 computed / 模板内直接使用。
 */
export function useEngineCaps() {
  ensureCaps().catch(() => { /* 拉取失败静默：按 full 降级，不阻塞页面 */ })
  const instanceCtx = useInstanceContext()

  function cap(key: string): CapabilityLevel {
    const engine = instanceCtx.currentEngine
    if (!engine || !caps.value) return 'full'
    return caps.value[engine]?.[key] ?? 'none'
  }

  return { cap, capsReady: computed(() => caps.value != null) }
}
