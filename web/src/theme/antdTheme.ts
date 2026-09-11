/**
 * AntDV 主题配置（暗色外壳 + 浅色工作台）。
 * - workbenchTheme：全局挂载（App.vue 的 a-config-provider）——内容页/弹层/消息走浅色工作台；
 *   外壳里的 antd 组件（Layout/侧栏 Menu）通过 components token 指定暗色。
 * - chromeTheme：暗色区块（登录页、顶栏标签条）嵌套 a-config-provider 用，darkAlgorithm。
 */
import { theme } from 'ant-design-vue'
import { FONT_FAMILY, palette } from './palette'

/** 全局：浅色工作台 */
export const workbenchTheme = {
  algorithm: theme.defaultAlgorithm,
  token: {
    colorPrimary: palette.brand.primary,
    colorInfo: palette.brand.primary,
    colorSuccess: palette.success.text,
    colorWarning: '#faad14',
    colorError: palette.danger.text,
    colorBgLayout: palette.bg.body,
    borderRadius: 4,
    fontSize: 14,
    fontFamily: FONT_FAMILY,
  },
  components: {
    Layout: {
      headerBg: palette.chrome.bg,
      siderBg: palette.chrome.bg,
      bodyBg: palette.bg.body,
    },
    // 侧栏菜单：透明底融入外壳 + 主色淡底选中态（替代默认 #001529 深蓝）
    Menu: {
      darkItemBg: 'transparent',
      darkSubItemBg: 'transparent',
      darkItemColor: palette.chrome.textSecondary,
      darkItemHoverColor: palette.chrome.textPrimary,
      darkItemHoverBg: 'rgba(255,255,255,.04)',
      darkItemSelectedBg: palette.chrome.primaryBg,
      darkItemSelectedColor: palette.chrome.primary,
      darkGroupColor: palette.chrome.textTertiary,
    },
    Table: {
      headerBg: '#fafafa',
      headerColor: palette.text.secondary,
      borderColor: palette.border.split,
      headerSplitColor: 'transparent',
    },
  },
}

/** 暗色外壳：登录页 / 顶栏标签条等暗色区块嵌套用 */
export const chromeTheme = {
  algorithm: theme.darkAlgorithm,
  token: {
    colorPrimary: palette.chrome.primary,
    colorInfo: palette.chrome.primary,
    colorBgBase: palette.chrome.bg,
    colorBgContainer: palette.chrome.panel,
    colorBorder: palette.chrome.border,
    colorBorderSecondary: palette.chrome.borderSplit,
    colorTextBase: palette.chrome.textPrimary,
    borderRadius: 4,
    fontFamily: FONT_FAMILY,
  },
}
