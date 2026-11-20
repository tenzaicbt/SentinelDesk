const TOKEN_KEY = 'sd-token'

export const tokenStore = {
  get: () => sessionStorage.getItem(TOKEN_KEY),
  set: (t: string) => sessionStorage.setItem(TOKEN_KEY, t),
  clear: () => sessionStorage.removeItem(TOKEN_KEY),
}

export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

let onUnauthorized: (() => void) | null = null
export const setUnauthorizedHandler = (fn: () => void) => { onUnauthorized = fn }

async function request<T>(method: string, url: string, body?: unknown, token?: string): Promise<T> {
  const headers: Record<string, string> = {}
  const t = token ?? tokenStore.get()
  if (t) headers.Authorization = `Bearer ${t}`
  let payload: BodyInit | undefined
  if (body instanceof FormData) payload = body
  else if (body !== undefined) {
    headers['Content-Type'] = 'application/json'
    payload = JSON.stringify(body)
  }
  const res = await fetch(url, { method, headers, body: payload })
  if (res.status === 401 && !url.startsWith('/api/auth/login') && !url.startsWith('/api/auth/mfa')) {
    onUnauthorized?.()
  }
  if (!res.ok) {
    let msg = res.statusText
    try {
      const j = await res.json()
      msg = j.detail || j.title || msg
    } catch { /* not json */ }
    if (res.status === 403 && msg === 'Forbidden') msg = 'You do not have permission to do this.'
    throw new ApiError(res.status, msg)
  }
  const ct = res.headers.get('content-type') || ''
  return (ct.includes('json') ? res.json() : undefined) as Promise<T>
}

export const api = {
  get: <T>(url: string) => request<T>('GET', url),
  post: <T = void>(url: string, body?: unknown, token?: string) => request<T>('POST', url, body ?? {}, token),
  put: <T = void>(url: string, body?: unknown) => request<T>('PUT', url, body),
  del: <T = void>(url: string) => request<T>('DELETE', url),
}

/** Authenticated file download (CSV exports). */
export async function download(url: string) {
  const res = await fetch(url, { headers: { Authorization: `Bearer ${tokenStore.get()}` } })
  if (!res.ok) throw new ApiError(res.status, 'Export failed')
  const blob = await res.blob()
  const name = /filename="?([^";]+)/.exec(res.headers.get('content-disposition') || '')?.[1] || 'export.csv'
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = name
  a.click()
  setTimeout(() => URL.revokeObjectURL(a.href), 2000)
}

/** POST that returns a file (e.g. the configured installer). */
export async function downloadPost(url: string, body: unknown) {
  const res = await fetch(url, {
    method: 'POST',
    headers: { Authorization: `Bearer ${tokenStore.get()}`, 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })
  if (!res.ok) {
    let msg = res.statusText
    try { const j = await res.json(); msg = j.detail || j.title || msg } catch { /* not json */ }
    throw new ApiError(res.status, msg)
  }
  const blob = await res.blob()
  const name = /filename="?([^";]+)/.exec(res.headers.get('content-disposition') || '')?.[1] || 'SentinelDesk-Setup.exe'
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = name
  a.click()
  setTimeout(() => URL.revokeObjectURL(a.href), 5000)
  return name
}

export const qs =(o: Record<string, string | number | boolean | undefined | null>) => {
  const p = new URLSearchParams()
  Object.entries(o).forEach(([k, v]) => { if (v !== undefined && v !== null && v !== '') p.set(k, String(v)) })
  const s = p.toString()
  return s ? `?${s}` : ''
}
