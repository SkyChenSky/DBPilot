import { onActivated, onBeforeUnmount, onDeactivated, ref } from 'vue'

export interface UsePollingOptions {
  /** 是否立即执行一次，默认 true */
  immediate?: boolean
  /** 页面隐藏（document.hidden）时自动暂停，默认 true */
  pauseOnHidden?: boolean
}

/**
 * 通用轮询组合式函数。
 * - interval 毫秒间隔执行 fn：链式 setTimeout（上一轮完成后才排下一轮），慢请求不重叠、不堆积；
 * - pauseOnHidden 时，document.hidden 期间自动跳过执行（不累积），但仍然继续排程；
 * - 多标签页 keep-alive 场景：组件被切走（deactivated）自动暂停，切回恢复；
 * - 组件卸载时自动清理定时器。
 */
export function usePolling(
  fn: () => void | Promise<void>,
  interval = 10_000,
  options: UsePollingOptions = {},
) {
  const { immediate = true, pauseOnHidden = true } = options
  const isActive = ref(false)
  let timer: ReturnType<typeof setTimeout> | null = null
  let resumeOnActivate = false
  // 轮询代际：stop→start 快速切换时，旧周期在途的 finally 不再排程（防双定时器）
  let generation = 0

  const clear = () => {
    if (timer !== null) {
      clearTimeout(timer)
      timer = null
    }
  }

  const run = () => Promise.resolve(fn()).catch(() => undefined)

  // 上一轮 run 完成后（finally）再排下一次，执行期间不重叠
  const runAndSchedule = (gen: number) => {
    run().finally(() => {
      if (isActive.value && gen === generation) timer = setTimeout(tick, interval)
    })
  }

  const tick = () => {
    timer = null
    if (pauseOnHidden && document.hidden) {
      // 页面隐藏：跳过本次执行，但仍排下一轮（切回可见即恢复）
      timer = setTimeout(tick, interval)
      return
    }
    runAndSchedule(generation)
  }

  const start = () => {
    clear()
    const gen = ++generation
    isActive.value = true
    if (immediate) runAndSchedule(gen)
    else timer = setTimeout(tick, interval)
  }

  const stop = () => {
    clear()
    isActive.value = false
  }

  // keep-alive 钩子（组件不在 keep-alive 内时为 no-op）
  onDeactivated(() => {
    resumeOnActivate = isActive.value
    if (isActive.value) stop()
  })
  onActivated(() => {
    if (resumeOnActivate && !isActive.value) start()
  })

  onBeforeUnmount(stop)

  return { isActive, start, stop }
}
