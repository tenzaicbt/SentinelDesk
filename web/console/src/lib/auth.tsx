import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react'
import { api, setUnauthorizedHandler, tokenStore } from './api'

export type Role = 'SuperAdmin' | 'BranchAdmin' | 'Auditor' | 'HrViewer'

export interface Me {
  id: string
  username: string
  displayName: string
  email?: string
  role: Role
  mfaEnabled: boolean
  mustChangePassword: boolean
  orgUnitScope?: string
  scopeName?: string
}

interface AuthCtx {
  me: Me | null
  loading: boolean
  login: (token: string) => Promise<void>
  logout: () => void
  refresh: () => Promise<void>
  can: { write: boolean; super: boolean; web: boolean; titles: boolean; audit: boolean; live: boolean }
}

const Ctx = createContext<AuthCtx>(null!)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null)
  const [loading, setLoading] = useState(true)

  const logout = useCallback(() => {
    tokenStore.clear()
    setMe(null)
  }, [])

  const refresh = useCallback(async () => {
    if (!tokenStore.get()) { setLoading(false); return }
    try { setMe(await api.get<Me>('/api/auth/me')) }
    catch { tokenStore.clear(); setMe(null) }
    finally { setLoading(false) }
  }, [])

  useEffect(() => {
    setUnauthorizedHandler(logout)
    refresh()
  }, [logout, refresh])

  const login = async (token: string) => {
    tokenStore.set(token)
    await refresh()
  }

  const r = me?.role
  const can = {
    write: r === 'SuperAdmin' || r === 'BranchAdmin',
    super: r === 'SuperAdmin',
    web: r !== 'HrViewer',
    titles: r === 'SuperAdmin' || r === 'BranchAdmin',
    audit: r === 'SuperAdmin' || r === 'Auditor',
    live: r !== 'HrViewer',
  }
  return <Ctx.Provider value={{ me, loading, login, logout, refresh, can }}>{children}</Ctx.Provider>
}

export const useAuth = () => useContext(Ctx)
