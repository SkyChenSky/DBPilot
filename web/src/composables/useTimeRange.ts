import { ref } from 'vue'
import dayjs, { type Dayjs } from 'dayjs'
import { message } from 'ant-design-vue'

/** 时间档位（相对"最近 X 分钟"） */
export interface TimeRangePreset {
  value: string
  label: string
  minutes: number
}

/** 标准档位（慢SQL/阻塞/死锁等页面通用） */
export const DEFAULT_TIME_PRESETS: TimeRangePreset[] = [
  { value: '5m', label: '最近 5 分钟', minutes: 5 },
  { value: '30m', label: '最近 30 分钟', minutes: 30 },
  { value: '1h', label: '最近 1 小时', minutes: 60 },
  { value: '2h', label: '最近 2 小时', minutes: 120 },
  { value: '6h', label: '最近 6 小时', minutes: 360 },
  { value: '24h', label: '最近 24 小时', minutes: 1440 },
  { value: '3d', label: '最近 3 天', minutes: 4320 },
  { value: '7d', label: '最近 7 天', minutes: 10080 },
]

/**
 * 时间范围筛选组合式：档位下拉 ↔ 自定义区间双向联动（统一交互）。
 * - 选任意档位：把对应 [now-X, now] 同步进自定义区间控件（常驻显示当前实际范围）
 * - 手改区间：下拉自动切"自定义"
 * - 自定义跨度不得超过 presets 中最大档位（超出则警告并把结束截断到 开始+上限）
 * - 档位选择 / 区间确认后回调 onApply（页面传 search 触发查询）
 */
export function useTimeRange(opts: {
  presets?: TimeRangePreset[]
  defaultKey?: string
  onApply?: () => void
} = {}) {
  const presets = opts.presets ?? DEFAULT_TIME_PRESETS
  const rangeOptions = [...presets.map(({ value, label }) => ({ value, label })), { value: 'custom', label: '自定义' }]
  const minutes: Record<string, number> = Object.fromEntries(presets.map(p => [p.value, p.minutes]))
  const maxPreset = presets.reduce((a, b) => (b.minutes > a.minutes ? b : a))

  const rangeKey = ref(opts.defaultKey ?? '1h')
  /** 初始即显示默认档位对应区间（而非等首次选择才填充）；清空后为 undefined */
  const customRange = ref<[Dayjs, Dayjs] | undefined>([dayjs().subtract(minutes[rangeKey.value] ?? 60, 'minute'), dayjs()])

  /** 档位下拉 @change：同步区间到 picker 后触发查询 */
  function onPresetChange(key: string) {
    rangeKey.value = key
    const m = minutes[key]
    if (m !== undefined)
      customRange.value = [dayjs().subtract(m, 'minute'), dayjs()]
    opts.onApply?.()
  }

  /** 自定义区间 @change：切自定义 + 跨度校验后触发查询；清空仅重置不查询兜底由页面处理 */
  function onCustomChange(dates: [Dayjs, Dayjs] | null) {
    rangeKey.value = 'custom'
    if (!dates) {
      customRange.value = undefined
      return
    }
    if (dates[1].diff(dates[0], 'minute') > maxPreset.minutes) {
      message.warning(`自定义时间跨度不能大于${maxPreset.label}`)
      customRange.value = [dates[0], dates[0].add(maxPreset.minutes, 'minute')]
    } else {
      customRange.value = dates
    }
    opts.onApply?.()
  }

  /** 下传 API 的 from/to（ISO）；custom 未选完整时返回 undefined（API 侧按不限处理） */
  function fromTo(): { from?: string; to?: string } {
    if (rangeKey.value === 'custom')
      return { from: customRange.value?.[0].toISOString(), to: customRange.value?.[1].toISOString() }
    const m = minutes[rangeKey.value] ?? 60
    return { from: dayjs().subtract(m, 'minute').toISOString(), to: dayjs().toISOString() }
  }

  /** 禁选未来日期（自定义区间不允许超出当前时间） */
  const disabledDate = (d: Dayjs) => d.isAfter(dayjs(), 'day')

  return { rangeKey, rangeOptions, customRange, onPresetChange, onCustomChange, fromTo, disabledDate }
}
