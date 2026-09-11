import { defineStore } from 'pinia'

export interface AppTab {
  /** 完整路径（带参数的路由各开一个 tab） */
  key: string
  title: string
  closable: boolean
}

/**
 * 多标签页状态（点击菜单开 tab，方便切换）。
 * 首页 tab 常驻不可关闭；登出时 clear()。
 */
export const useTabsStore = defineStore('app-tabs', {
  state: () => ({
    tabs: [] as AppTab[],
    activeKey: '',
  }),
  actions: {
    /** 常驻 tab（如首页）：不存在则加入，不改变 activeKey */
    ensure(key: string, title: string) {
      if (!this.tabs.some(t => t.key === key))
        this.tabs.push({ key, title, closable: false })
    },
    /** 激活 tab：不存在则追加 */
    add(key: string, title: string, closable = true) {
      if (!this.tabs.some(t => t.key === key))
        this.tabs.push({ key, title, closable })
      this.activeKey = key
    },
    /** 关闭 tab；若关闭的是当前激活 tab，返回应切换到的 key（无可切返回 null） */
    remove(key: string): string | null {
      const idx = this.tabs.findIndex(t => t.key === key)
      if (idx < 0 || !this.tabs[idx].closable) return null

      this.tabs.splice(idx, 1)
      if (this.activeKey === key) {
        const next = this.tabs[Math.min(idx, this.tabs.length - 1)]
        this.activeKey = next?.key ?? ''
      }
      return this.activeKey || null
    },
    clear() {
      this.tabs = []
      this.activeKey = ''
    },
  },
})
