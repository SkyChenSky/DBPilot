import { http } from './http'

export interface AuthUser {
  username: string
}

export async function login(username: string, password: string): Promise<AuthUser> {
  const resp = await http.post<{ code: number; message: string; data: AuthUser }>(
    '/login',
    { username, password },
  )
  return resp.data.data
}

export async function logout(): Promise<void> {
  await http.post('/logout')
}

export async function fetchMe(): Promise<AuthUser> {
  const resp = await http.get<{ code: number; message: string; data: AuthUser }>('/me')
  return resp.data.data
}
