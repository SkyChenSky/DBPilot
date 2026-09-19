/**
 * 色值单一来源（TS 侧）。整体结构：
 *   暗色外壳（侧栏/顶栏/标签条/登录页） + 浅色工作台（内容画布，AntDV 默认浅色系微调）。
 * variables.css 为本文件的手工镜像（CSS 变量），改这里必须同步那边（文件头有互链注释）。
 */
export const FONT_FAMILY =
  "-apple-system, BlinkMacSystemFont, 'Segoe UI', 'PingFang SC', 'Hiragino Sans GB', 'Microsoft YaHei', sans-serif"

export const palette = {
  /** 暗色外壳：侧栏/顶栏/标签条/登录页 */
  chrome: {
    bg: '#10141d',
    bgDeep: '#0b0e14',
    panel: '#1b2331',
    border: '#2a3446',
    borderSplit: '#202a3a',
    textPrimary: '#e8eef7',
    textSecondary: '#9daabb',
    textTertiary: '#6e7d92',
    textDisabled: '#4b586c',
    primary: '#3c9eff',
    primaryBg: 'rgba(60,158,255,.12)',
    cyan: '#36cfc9',
  },
  /** 浅色工作台：内容画布 / 卡片面板 / 弹层 */
  bg: {
    body: '#f0f2f5',
    panel: '#ffffff',
    panelElevated: '#ffffff',
    code: '#f6f8fa',
  },
  border: {
    strong: '#d9dce3',
    split: '#f0f0f0',
  },
  text: {
    primary: '#26303e',
    secondary: '#5c6b80',
    tertiary: '#8a97a8',
    disabled: '#bcc4cf',
  },
  brand: {
    primary: '#1677ff',
    primaryBg: '#e6f4ff',
    cyan: '#13c2c2',
    /** 事件标注专用金黄（仅 JS/图表消费，CSS 无镜像）：比 warning 橙更远离 danger 红 */
    gold: '#faad14',
  },
  /** 语义三件套（text/bg/border），浅色工作台口径 */
  success: { text: '#52c41a', bg: '#f6ffed', border: '#b7eb8f' },
  warning: { text: '#fa8c16', bg: '#fff7e6', border: '#ffd591' },
  danger: { text: '#f5222d', bg: '#fff1f0', border: '#ffa39e' },
  purple: { text: '#722ed1', bg: '#f9f0ff', border: '#d3adf7' },
} as const
