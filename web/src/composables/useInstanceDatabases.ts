import { computed, ref, watch } from 'vue'
import { storeToRefs } from 'pinia'
import { listDatabases } from '../api/instance'
import { useInstanceContext } from '../stores/instanceContext'

/**
 * 全局实例上下文 + 库下拉两级接线（索引诊断 / Top SQL / 慢日志等带库筛选的页面复用）。
 * - 实例不再页内选择：instanceId/instances 来自 instanceContext store（顶栏统一切换）；
 * - onMounted 调 init()：store 已就绪直接拉库 + 回调首查；未就绪走 ensureLoaded（instanceId 赋值触发 watch 接力）；
 * - 顶栏切换实例 → 自动重拉库列表并回调 onInstanceChange（页面重查）；
 * - dbOptions 含首位"全部库"选项（value=''，后端遍历非系统库聚合）。
 */
export function useInstanceDatabases(onInstanceChange?: () => void) {
  const ctx = useInstanceContext()
  const { instances, instanceId } = storeToRefs(ctx)
  const databases = ref<string[]>([])
  const dbName = ref<string>()
  const dbLoading = ref(false)

  async function init() {
    if (ctx.loaded) {
      await loadDatabases()
      onInstanceChange?.()
      return
    }
    await ctx.ensureLoaded()   // undefined → 值 的赋值由下方 watch 接力首查
  }

  watch(instanceId, async () => {
    await loadDatabases()
    onInstanceChange?.()
  })

  async function loadDatabases() {
    databases.value = []
    dbName.value = undefined
    if (!instanceId.value) return
    dbLoading.value = true
    try {
      databases.value = await listDatabases(instanceId.value)
      // 默认选中"全部库"（全站口径；需要具体库时用户自行切换）
      dbName.value = ''
    } finally {
      dbLoading.value = false
    }
  }

  /** 下拉选项：首位"全部库"（空值 = 后端遍历全部非系统库） */
  const dbOptions = computed(() => [
    { value: '', label: '全部库' },
    ...databases.value.map(d => ({ value: d, label: d })),
  ])

  return { instances, instanceId, databases, dbName, dbLoading, dbOptions, init, loadDatabases }
}
