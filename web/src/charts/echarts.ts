/**
 * ECharts 集中注册 + 主题（全站唯一样式来源，浅色工作台口径）。
 * 页面不要再自行 echarts.use / 手写轴色 tooltip 色，统一：
 *   import { initChart, CHART_COLORS, CHART_SERIES } from '../charts/echarts'
 *   或生命周期走 composables/useChart.ts 的 useCharts。
 */
import * as echarts from 'echarts/core'
import { BarChart, GraphChart, LineChart } from 'echarts/charts'
import { GraphicComponent, GridComponent, LegendComponent, MarkLineComponent, TooltipComponent } from 'echarts/components'
import { CanvasRenderer } from 'echarts/renderers'
import { FONT_FAMILY, palette } from '../theme/palette'

// MarkLineComponent：markLine（趋势页事件叠加虚线）是独立组件，漏注册时静默不画（无报错）
echarts.use([LineChart, BarChart, GraphChart, GraphicComponent, GridComponent, LegendComponent, MarkLineComponent, TooltipComponent, CanvasRenderer])

export { echarts }
export type { EChartsCoreOption } from 'echarts/core'

/** 语义色：固定含义的系列用这些（死锁/错误=danger、等待=warning、CPU/主指标=primary、IO=cyan） */
export const CHART_COLORS = {
  primary: palette.brand.primary,
  cyan: palette.brand.cyan,
  success: palette.success.text,
  warning: palette.warning.text,
  danger: palette.danger.text,
  purple: palette.purple.text,
} as const

/** 轮转色板：无固定语义的多系列（如性能洞察负载构成 18 色）按序取色，超出循环 */
export const CHART_SERIES = [
  '#1677ff', '#13c2c2', '#52c41a', '#faad14', '#f5222d',
  '#722ed1', '#eb2f96', '#fa8c16', '#a0d911', '#597ef7',
] as const

export const CHART_THEME = 'dbpilot'

const axisCommon = {
  axisLine: { show: true, lineStyle: { color: palette.border.strong } },
  axisTick: { lineStyle: { color: palette.border.strong } },
  axisLabel: { color: palette.text.tertiary },
  splitLine: { lineStyle: { color: palette.border.split } },
  splitArea: { show: false },
}

echarts.registerTheme(CHART_THEME, {
  color: [...CHART_SERIES],
  backgroundColor: 'transparent',
  textStyle: { color: palette.text.secondary, fontFamily: FONT_FAMILY },
  legend: { textStyle: { color: palette.text.secondary } },
  tooltip: {
    backgroundColor: '#ffffff',
    borderColor: palette.border.strong,
    borderWidth: 1,
    textStyle: { color: palette.text.primary, fontSize: 12 },
    extraCssText: 'box-shadow: 0 4px 16px rgba(31,45,61,.12);',
  },
  categoryAxis: axisCommon,
  valueAxis: axisCommon,
  timeAxis: axisCommon,
  logAxis: axisCommon,
})

/** 统一 init 入口：自动挂 dbpilot 主题 */
export function initChart(el: HTMLElement) {
  return echarts.init(el, CHART_THEME)
}

/** v-if 分支翻转后容器是重建的新 DOM：句柄绑的 DOM 已不是当前容器（或已 dispose）时释放，
 * 返回 null 交给调用方 `chart = rebindChart(chart, el) ?? initChart(el)` 重建——否则 ??= 会跳过 */
export function rebindChart<T extends { isDisposed(): boolean; getDom(): HTMLElement; dispose(): void } | null>(
  chart: T,
  el: HTMLElement | undefined,
): T | null {
  if (chart && (chart.isDisposed() || chart.getDom() !== el)) {
    chart.dispose()
    return null
  }
  return chart
}
