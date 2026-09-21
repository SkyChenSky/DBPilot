import dayjs from 'dayjs'

/**
 * 全站统一格式化函数（原各页 22 处局部 fmt* 收敛于此）。
 * 空值占位统一 '-'；表格时间列约定带年份完整格式。
 */

/** 时间：默认完整格式 'YYYY-MM-DD HH:mm:ss'；其他形态传 fmt（如 'HH:mm:ss'） */
export function fmtTime(t?: string | null, fmt = 'YYYY-MM-DD HH:mm:ss') {
  return t ? dayjs(t).format(fmt) : '-'
}

/** 时间：分钟档（心跳/快照/编译时刻等不关心秒的场景） */
export function fmtTimeMinute(t?: string | null) {
  return fmtTime(t, 'YYYY-MM-DD HH:mm')
}

/** 毫秒时长（带单位）：≥10s 显示秒 1 位小数，否则 `1,234ms`（表格/文本场景通用，值自带单位） */
export function fmtMsUnit(ms?: number | null) {
  if (ms == null) return '-'
  if (ms >= 10_000) return `${(ms / 1000).toFixed(1)}s`
  return `${Math.round(ms).toLocaleString()}ms`
}

/** 秒时长三段式：`45s` / `1m40s` / `1h30m`（floor 语义，不用 round） */
export function fmtDur(s: number) {
  if (s < 90) return `${s}s`
  if (s < 5400) return `${Math.floor(s / 60)}m${s % 60}s`
  return `${Math.floor(s / 3600)}h${Math.floor((s % 3600) / 60)}m`
}

/** 毫秒 → 秒（两位小数，慢SQL 阿里云口径） */
export function fmtSec(ms?: number | null) {
  return ms == null ? '-' : (ms / 1000).toFixed(2)
}

/** 数值千分位；maxFractionDigits 控制小数上限 */
export function fmtNum(n?: number | null, maxFractionDigits = 0) {
  return n == null ? '-' : n.toLocaleString(undefined, { maximumFractionDigits: maxFractionDigits })
}

/** 平均值（保留 1 位小数） */
export function fmtAvg(n?: number | null) {
  return fmtNum(n, 1)
}

/** 占比后缀（分母 = 本次查询全部行合计，TopN 内之和可 < 100%）；固定 2 位小数，
 *  尾巴形态恒为 "| 45.67%"（8~10 字符）——带尾巴的表格列宽按「主值约 12 字符 + 尾巴」预算 150px；
 *  0 / NULL 不显示 */
export function fmtPct(p?: number | null) {
  return p != null && p > 0 ? `| ${p.toFixed(2)}%` : ''
}
