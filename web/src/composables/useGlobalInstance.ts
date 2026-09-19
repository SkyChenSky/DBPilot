import { watch } from 'vue'
import { storeToRefs } from 'pinia'
import { useInstanceContext } from '../stores/instanceContext'

/**
 * 全局实例上下文接线（无库维度的页面用：MetricsTrend / PerformanceInsight / Blocking / DeadlockList）。
 * - onMounted 调 init()：store 已就绪直接回调一次首查；未就绪走 ensureLoaded（instanceId 赋值会触发 watch 完成首查）；
 * - 顶栏切换实例（任意页面）→ onInstanceChange 自动回调重查。
 */
export function useGlobalInstance(onInstanceChange?: () => void) {
  const ctx = useInstanceContext()
  const { instances, instanceId } = storeToRefs(ctx)

  async function init() {
    if (ctx.loaded) {
      onInstanceChange?.()
      return
    }
    await ctx.ensureLoaded()   // undefined → 值 的赋值由下方 watch 接力首查
  }

  if (onInstanceChange) watch(instanceId, () => onInstanceChange())

  return { instances, instanceId, init }
}
