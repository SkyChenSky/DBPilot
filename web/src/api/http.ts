import axios from 'axios'
import { message } from 'ant-design-vue'

/** 后端统一响应结构（对应 DBPilot.Abstractions.ApiResponse） */
export interface ApiBody<T = unknown> {
  code: number
  message: string
  data: T
}

export const http = axios.create({
  baseURL: '/api',
  timeout: 30_000,
})

http.interceptors.response.use(
  (response) => {
    const body = response.data as ApiBody
    if (body && typeof body.code === 'number' && body.code !== 0) {
      message.error(body.message || '请求失败')
      return Promise.reject(new Error(body.message))
    }
    return response
  },
  (error) => {
    const status = error?.response?.status
    const url: string = error?.config?.url ?? ''

    // 401：会话失效 → 跳登录页（登录请求本身除外，避免循环）
    if (status === 401 && !url.includes('/login')) {
      // 静默处理：未跳转的场景（如登录页上的会话探测 /me）弹"未登录"纯属噪音；
      // 登录接口自身的 401（密码错误）落到下方 message 正常提示
      if (!window.location.pathname.startsWith('/login')) {
        window.location.href = '/login'
      }
      return Promise.reject(error)
    }

    const msg = error?.response?.data?.message || error.message || '网络异常'
    message.error(msg)
    return Promise.reject(error)
  },
)

/** GET：自动解包统一响应，直接返回 data */
export async function apiGet<T>(url: string, params?: Record<string, unknown>): Promise<T> {
  const resp = await http.get<ApiBody<T>>(url, { params })
  return resp.data.data
}

/** POST：自动解包统一响应，直接返回 data */
export async function apiPost<T>(url: string, body?: unknown): Promise<T> {
  const resp = await http.post<ApiBody<T>>(url, body)
  return resp.data.data
}

/** PUT：自动解包统一响应，直接返回 data */
export async function apiPut<T>(url: string, body?: unknown): Promise<T> {
  const resp = await http.put<ApiBody<T>>(url, body)
  return resp.data.data
}

/** DELETE：自动解包统一响应，直接返回 data */
export async function apiDelete<T>(url: string): Promise<T> {
  const resp = await http.delete<ApiBody<T>>(url)
  return resp.data.data
}
