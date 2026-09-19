import { onActivated, onUnmounted } from 'vue'
import { initChart } from '../charts/echarts'
import type { EChartsCoreOption } from 'echarts/core'

type EChartsInstance = ReturnType<typeof initChart>

export interface UseChartsOptions {
  /** 懒渲染：容器进入可视区（rootMargin 提前量）才 init 并渲染，默认 true */
  lazy?: boolean
  /** IntersectionObserver 提前量（沿用原 MetricsTrend 的 100px） */
  rootMargin?: string
}

/** 按需产 option：返回 null/undefined 时跳过本次 setOption（数据未到位的空轴占位） */
type OptionFactory = (key: string) => EChartsCoreOption | null | undefined

/**
 * 图表生命周期统一封装（吸收 MetricsTrend 四件套语义）：
 * bind(key) 绑定容器 → IntersectionObserver 懒 init + ResizeObserver 自动 resize
 * + keep-alive 切回兜底（onActivated resize / 容器宽度从 0 恢复时补建补绘），卸载自动 dispose。
 * 单图场景 key 省略（bind() / render()）；多图按 key 区分（如 MetricsTrend 11 张指标卡）。
 */
export function useCharts(getOption: OptionFactory, options: UseChartsOptions = {}) {
  const { lazy = true, rootMargin = '100px 0px' } = options

  interface Entry {
    el: HTMLElement
    chart: EChartsInstance | null
    visible: boolean
  }
  const entries = new Map<string, Entry>()
  /** 缓存函数 ref：模板重渲染复用同一函数，避免 Vue 触发 null→el 的 detach/reattach 折腾 */
  const bound = new Map<string, (el: unknown) => void>()
  let io: IntersectionObserver | null = null
  let ro: ResizeObserver | null = null

  function keyOf(el: HTMLElement): string | undefined {
    for (const [key, entry] of entries) {
      if (entry.el === el) return key
    }
    return undefined
  }

  function ensureChart(key: string): EChartsInstance | null {
    const entry = entries.get(key)!
    if (entry.chart && !entry.chart.isDisposed()) return entry.chart
    // 容器尚未布局（隐藏页签/折叠分组宽度 0）：不 init，等 ResizeObserver 兜底补建
    if (entry.el.clientWidth === 0 || entry.el.clientHeight === 0) return null
    entry.chart = initChart(entry.el)
    return entry.chart
  }

  /** 渲染单个图（懒渲染模式下未入视口的跳过，等滚动到再渲染） */
  function render(key = ''): void {
    const entry = entries.get(key)
    if (!entry) return
    if (lazy && !entry.visible) return
    const chart = ensureChart(key)
    if (!chart) return
    const option = getOption(key)
    if (option) chart.setOption(option, true)
  }

  /** 数据刷新后重绘所有已入视口的图（未见的等滚动到再渲染） */
  function renderVisible(): void {
    for (const [key, entry] of entries) {
      if (entry.visible) render(key)
    }
  }

  function resize(): void {
    for (const entry of entries.values()) {
      if (entry.chart && !entry.chart.isDisposed()) entry.chart.resize()
    }
  }

  function disposeEntry(key: string): void {
    const entry = entries.get(key)
    if (!entry) return
    io?.unobserve(entry.el)
    ro?.unobserve(entry.el)
    if (entry.chart && !entry.chart.isDisposed()) entry.chart.dispose()
    entries.delete(key)
  }

  function attach(key: string, el: HTMLElement): void {
    const existed = entries.get(key)
    if (existed && existed.el === el) return
    if (existed) disposeEntry(key)
    entries.set(key, { el, chart: null, visible: !lazy })
    io?.observe(el)
    ro?.observe(el)
    if (!lazy) render(key)
  }

  /** 模板函数 ref：:ref="bind('cpu')"（单图 bind()）；内部缓存见 bound 注释 */
  function bind(key = ''): (el: unknown) => void {
    let fn = bound.get(key)
    if (!fn) {
      fn = (el: unknown) => {
        if (el) attach(key, el as HTMLElement)
        else disposeEntry(key)
      }
      bound.set(key, fn)
    }
    return fn
  }

  io =
    typeof IntersectionObserver !== 'undefined'
      ? new IntersectionObserver(
          (list) => {
            for (const item of list) {
              if (!item.isIntersecting) continue
              const key = keyOf(item.target as HTMLElement)
              if (key === undefined) continue
              const entry = entries.get(key)!
              if (entry.visible) continue
              entry.visible = true
              render(key)
            }
          },
          { rootMargin },
        )
      : null

  ro =
    typeof ResizeObserver !== 'undefined'
      ? new ResizeObserver((list) => {
          for (const item of list) {
            const key = keyOf(item.target as HTMLElement)
            if (key === undefined) continue
            const entry = entries.get(key)!
            if (entry.chart && !entry.chart.isDisposed()) entry.chart.resize()
            // 宽度从 0 恢复（keep-alive 切回/折叠展开）：已可见但未建图的补建补绘
            else if (entry.visible && item.contentRect.width > 0) render(key)
          }
        })
      : null

  // keep-alive 切回：主动 resize 防旧尺寸（宽度恢复兜底在 ResizeObserver）
  onActivated(() => resize())

  onUnmounted(() => {
    io?.disconnect()
    io = null
    ro?.disconnect()
    ro = null
    for (const key of [...entries.keys()]) disposeEntry(key)
    bound.clear()
  })

  return { bind, render, renderVisible, resize }
}
