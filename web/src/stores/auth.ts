import { defineStore } from 'pinia'
import { fetchMe, login as apiLogin, logout as apiLogout } from '../api/auth'

export const useAuthStore = defineStore('auth', {
  state: () => ({
    username: '' as string,
    checked: false, // 是否已完成会话探测
  }),
  getters: {
    isLoggedIn: (state) => state.username !== '',
  },
  actions: {
    /** 应用启动 / 路由守卫首次触发时探测会话 */
    async ensureChecked() {
      if (this.checked) return
      try {
        const user = await fetchMe()
        this.username = user.username
      } catch {
        this.username = ''
      } finally {
        this.checked = true
      }
    },
    async login(username: string, password: string) {
      const user = await apiLogin(username, password)
      this.username = user.username
      this.checked = true
    },
    async logout() {
      try {
        await apiLogout()
      } finally {
        this.username = ''
      }
    },
  },
})
