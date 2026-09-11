/**
 * 按内容自适应表格列宽：canvas 量测文本像素宽（与表格 14px 默认字体一致），
 * 加单元格左右 padding，clamp 到 [min, max]；空数据 / 无 canvas 回退 min。
 * 用法：时间列传完整格式的样例字符串，库列传当前页数据的全部值。
 */
export function fitColumnWidth(texts: Array<string | null | undefined>, opts: { min?: number; max?: number } = {}) {
  const { min = 80, max = 300 } = opts
  const ctx = document.createElement('canvas').getContext('2d')
  if (!ctx) return min
  ctx.font = '14px -apple-system, "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif'
  let w = 0
  for (const t of texts)
    if (t) w = Math.max(w, ctx.measureText(t).width)
  return Math.round(Math.min(max, Math.max(min, w + 24)))
}

/** 时间列宽常量（表格列宽约定①：定长内容按完整格式样例量宽，页面直接用不再各处重算） */
export const TIME_COL_W = fitColumnWidth(['2026-12-31 23:59:59'], { min: 140 })
export const MINUTE_COL_W = fitColumnWidth(['2026-12-31 23:59'], { min: 100 })
