import { useEffect, useState } from 'react'
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { toast } from 'sonner'
import {
  Activity, Bell, Building2, ChevronsUpDown, FileBarChart2, KeyRound, LayoutDashboard, LogOut, Monitor, Moon,
  PanelLeft, Radio, ScrollText, Search, SlidersHorizontal, Sun, UserCog, Users,
} from 'lucide-react'
import { useAuth } from '../lib/auth'
import { useHub, useHubEvent } from '../lib/hub'
import { api } from '../lib/api'
import { cx } from './ui'
import { CommandPalette } from './CommandPalette'
import brandIcon from '../assets/brand/icon.png'

const nav = [
  { section: 'Monitor', items: [
    { to: '/', label: 'Overview', icon: LayoutDashboard, end: true },
    { to: '/live', label: 'Live view', icon: Radio, perm: 'live' },
    { to: '/devices', label: 'Devices', icon: Monitor },
    { to: '/alerts', label: 'Alerts', icon: Bell, badge: true },
    { to: '/reports', label: 'Reports', icon: FileBarChart2 },
  ] },
  { section: 'Manage', items: [
    { to: '/policies', label: 'Policies', icon: SlidersHorizontal },
    { to: '/organization', label: 'Organization', icon: Building2 },
    { to: '/agents', label: 'Agent management', icon: KeyRound, perm: 'super' },
  ] },
  { section: 'Administration', items: [
    { to: '/users', label: 'Admin users', icon: Users, perm: 'super' },
    { to: '/audit', label: 'Audit log', icon: ScrollText, perm: 'audit' },
  ] },
] as const

const allItems: { to: string; label: string }[] = nav.flatMap((g) => g.items.map((i) => ({ to: i.to as string, label: i.label as string })))
function pageTitle(path: string) {
  if (path === '/') return 'Overview'
  if (path.startsWith('/profile')) return 'Profile & security'
  return allItems.filter((i) => i.to !== '/' && path.startsWith(i.to)).sort((a, b) => b.to.length - a.to.length)[0]?.label ?? 'SentinelDesk'
}

export function useTheme() {
  const [dark, setDark] = useState(() => document.documentElement.classList.contains('dark'))
  const toggle = () => {
    const d = !dark
    setDark(d)
    document.documentElement.classList.toggle('dark', d)
    document.documentElement.dataset.theme = d ? 'dark' : 'light'
    try { localStorage.setItem('sd-theme', d ? 'dark' : 'light') } catch { /* private mode */ }
  }
  return { dark, toggle }
}

const readCollapsed = () => { try { return localStorage.getItem('sd-sidebar') === 'closed' } catch { return false } }

export function Layout() {
  const { me, logout, can } = useAuth()
  const { connected } = useHub()
  const { dark, toggle } = useTheme()
  const [palette, setPalette] = useState(false)
  const [mobileNav, setMobileNav] = useState(false)
  const [collapsed, setCollapsed] = useState(readCollapsed)
  const [userMenu, setUserMenu] = useState(false)
  const navigate = useNavigate()
  const location = useLocation()

  const { data: alertCount, refetch } = useQuery({
    queryKey: ['alert-count'],
    queryFn: async () => (await api.get<unknown[]>('/api/alerts?status=open')).length,
    refetchInterval: 60_000,
  })

  useHubEvent<{ title: string; severity: string; hostname: string; deviceId: string }>('alert', (a) => {
    refetch()
    const fn = a.severity === 'critical' ? toast.error : a.severity === 'warning' ? toast.warning : toast.info
    fn(a.title, { description: a.hostname, action: { label: 'Open', onClick: () => navigate(`/devices/${a.deviceId}`) } })
  })

  useEffect(() => {
    const h = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') { e.preventDefault(); setPalette(true) }
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'b') { e.preventDefault(); toggleSidebar() }
    }
    window.addEventListener('keydown', h)
    return () => window.removeEventListener('keydown', h)
  })
  useEffect(() => { setMobileNav(false); setUserMenu(false) }, [location.pathname])

  function toggleSidebar() {
    if (window.matchMedia('(min-width: 1024px)').matches) {
      setCollapsed((c) => {
        try { localStorage.setItem('sd-sidebar', c ? 'open' : 'closed') } catch { /* private mode */ }
        return !c
      })
    } else setMobileNav((m) => !m)
  }

  const initials = me?.displayName?.split(' ').map((p) => p[0]).slice(0, 2).join('').toUpperCase()

  const sidebar = (
    <aside className="flex h-full w-64 flex-col bg-page text-fg dark:bg-panel">
      {/* header: team / product */}
      <div className="p-2">
        <NavLink to="/" className="flex h-12 items-center gap-2 rounded-md px-2 hover:bg-panel-2">
          <img src={brandIcon} alt="" className="size-8 shrink-0 object-contain" />
          <div className="grid flex-1 text-left leading-tight">
            <Wordmark className="text-[15px]" />
            <span className="truncate text-[10px] font-medium uppercase tracking-[0.18em] text-muted">Endpoint metrics</span>
          </div>
        </NavLink>
      </div>

      <nav className="flex-1 overflow-y-auto px-2">
        {nav.map((g) => {
          const items = g.items.filter((i) => !('perm' in i) || can[i.perm as keyof typeof can])
          if (!items.length) return null
          return (
            <div key={g.section} className="relative flex flex-col p-2">
              <div className="flex h-8 items-center px-2 text-xs font-medium text-muted">{g.section}</div>
              <ul className="flex flex-col gap-1">
                {items.map((i) => (
                  <li key={i.to}>
                    <NavLink
                      to={i.to}
                      end={'end' in i}
                      className={({ isActive }) => cx(
                        'flex h-8 items-center gap-2 rounded-md px-2 text-sm outline-none transition-colors focus-visible:ring-2 focus-visible:ring-ring',
                        isActive ? 'bg-brand-soft font-medium text-fg [&>svg]:text-brand' : 'text-fg-2 hover:bg-panel-2 hover:text-fg',
                      )}
                    >
                      <i.icon className="size-4 shrink-0" />
                      <span className="flex-1 truncate">{i.label}</span>
                      {'badge' in i && !!alertCount && (
                        <span className="flex h-5 min-w-5 items-center justify-center rounded-md bg-brand px-1 text-xs font-medium tabular text-white">{alertCount > 99 ? '99+' : alertCount}</span>
                      )}
                    </NavLink>
                  </li>
                ))}
              </ul>
            </div>
          )
        })}
      </nav>

      {/* footer: live status + user */}
      <div className="flex flex-col gap-2 p-2">
        <div className="flex items-center gap-2 px-2 text-xs text-muted">
          <span className={cx('size-1.5 rounded-full', connected ? 'bg-good' : 'bg-warning')} />
          {connected ? 'Live updates connected' : 'Reconnecting live updates…'}
        </div>
        <Credit className="px-2" />
        <div className="relative">
          <button onClick={() => setUserMenu((v) => !v)}
            className={cx('flex h-12 w-full items-center gap-2 rounded-md px-2 text-left text-sm hover:bg-panel-2', userMenu && 'bg-panel-2')}>
            <div className="grid size-8 shrink-0 place-items-center rounded-lg bg-panel-2 text-xs font-medium ring-1 ring-line">{initials}</div>
            <div className="grid flex-1 leading-tight">
              <span className="truncate font-medium">{me?.displayName}</span>
              <span className="truncate text-xs text-muted">{me?.role}{me?.scopeName ? ` · ${me.scopeName}` : ''}</span>
            </div>
            <ChevronsUpDown className="size-4 text-muted" />
          </button>
          {userMenu && (
            <div className="absolute inset-x-0 bottom-14 z-50 rounded-lg border border-line bg-panel p-1 text-fg shadow-md animate-rise" onMouseLeave={() => setUserMenu(false)}>
              <div className="flex items-center gap-2 px-1 py-1.5 text-left text-sm">
                <div className="grid size-8 place-items-center rounded-lg bg-panel-2 text-xs font-medium">{initials}</div>
                <div className="grid flex-1 leading-tight"><span className="truncate font-medium">{me?.displayName}</span><span className="truncate text-xs text-muted">{me?.username}</span></div>
              </div>
              <div className="-mx-1 my-1 h-px bg-line" />
              <MenuButton onClick={() => navigate('/profile')}><UserCog className="size-4" />Profile & security</MenuButton>
              <MenuButton onClick={toggle}>{dark ? <Sun className="size-4" /> : <Moon className="size-4" />}{dark ? 'Light mode' : 'Dark mode'}</MenuButton>
              <div className="-mx-1 my-1 h-px bg-line" />
              <MenuButton onClick={logout}><LogOut className="size-4" />Sign out</MenuButton>
            </div>
          )}
        </div>
      </div>
    </aside>
  )

  return (
    <div className="flex h-full bg-page dark:bg-panel">
      <div className={cx('hidden shrink-0 overflow-hidden transition-[width] duration-200 ease-linear lg:block', collapsed ? 'w-0' : 'w-64')}>{sidebar}</div>
      {mobileNav && (
        <div className="fixed inset-0 z-40 lg:hidden">
          <div className="absolute inset-0 bg-black/50" onClick={() => setMobileNav(false)} />
          <div className="absolute inset-y-0 left-0 border-r border-line shadow-lg animate-rise">{sidebar}</div>
        </div>
      )}

      {/* inset content */}
      <div className="flex min-w-0 flex-1 flex-col overflow-hidden bg-panel dark:bg-page lg:m-2 lg:rounded-xl lg:shadow-sm lg:ring-1 lg:ring-line">
        <header className="flex h-12 shrink-0 items-center gap-2 border-b border-line px-4 lg:px-6">
          <button onClick={toggleSidebar} className="-ml-1 grid size-7 place-items-center rounded-md text-fg hover:bg-panel-2" aria-label="Toggle sidebar" title="Toggle sidebar (Ctrl B)">
            <PanelLeft className="size-4" />
          </button>
          <div className="mx-2 h-4 w-px bg-line" />
          <h1 className="truncate text-base font-medium">{pageTitle(location.pathname)}</h1>
          <div className="ml-auto flex items-center gap-1.5">
            <button onClick={() => setPalette(true)}
              className="hidden h-8 w-64 items-center gap-2 rounded-md border border-line-strong bg-panel px-3 text-sm text-muted shadow-xs transition hover:bg-panel-2 dark:bg-white/4.5 md:flex">
              <Search className="size-4" />
              <span className="flex-1 text-left">Search…</span>
              <kbd className="rounded border border-line bg-panel-2 px-1.5 font-mono text-[10px] font-medium">Ctrl K</kbd>
            </button>
            <button onClick={() => setPalette(true)} className="grid size-8 place-items-center rounded-md hover:bg-panel-2 md:hidden" aria-label="Search"><Search className="size-4" /></button>
            <button onClick={toggle} className="grid size-8 place-items-center rounded-md hover:bg-panel-2" aria-label="Toggle theme" title="Toggle theme">
              {dark ? <Sun className="size-4" /> : <Moon className="size-4" />}
            </button>
            <button onClick={() => navigate('/alerts')} className="relative grid size-8 place-items-center rounded-md hover:bg-panel-2" aria-label="Alerts">
              <Bell className="size-4" />
              {!!alertCount && <span className="absolute right-1.5 top-1.5 size-2 rounded-full bg-critical ring-2 ring-panel dark:ring-page" />}
            </button>
          </div>
        </header>
        <main className="flex-1 overflow-y-auto">
          <div className="mx-auto w-full max-w-[1800px] px-3 py-3 lg:px-4">
            <Outlet />
          </div>
        </main>
      </div>
      <CommandPalette open={palette} onClose={() => setPalette(false)} />
    </div>
  )
}

function MenuButton({ onClick, children }: { onClick: () => void; children: React.ReactNode }) {
  return (
    <button onClick={onClick} className="flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-sm outline-none hover:bg-panel-2 [&_svg]:text-muted">
      {children}
    </button>
  )
}

/** Author credit (see LICENSE). */
export function Credit({ className }: { className?: string }) {
  return (
    <p className={cx('text-[11px] leading-relaxed text-muted', className)}>
      © 2026 SentinelDesk · Created &amp; developed by{' '}
      <a href="https://www.lordefx.com" target="_blank" rel="noreferrer" className="font-medium text-fg-2 hover:text-brand-ink hover:underline">Yohan Koshala</a>
      {' · '}
      <a href="https://github.com/tenzaicbt" target="_blank" rel="noreferrer" className="hover:text-brand-ink hover:underline">GitHub</a>
    </p>
  )
}

/** "Sentinel" in ink (white when inverse) + "Desk" in brand orange, as in doc/logo. */
export function Wordmark({ className, inverse }: { className?: string; inverse?: boolean }) {
  return (
    <span className={cx('truncate font-extrabold tracking-tight', inverse ? 'text-white' : 'text-fg', className)}>
      Sentinel<span className="text-brand">Desk</span>
    </span>
  )
}

export function Brand({ inverse, size = 'md' }: { inverse?: boolean; size?: 'md' | 'lg' }) {
  return (
    <div className="flex items-center gap-3">
      <img src={brandIcon} alt="" className={cx('shrink-0 object-contain', size === 'lg' ? 'size-12' : 'size-9')} />
      <div className="leading-tight">
        <Wordmark inverse={inverse} className={size === 'lg' ? 'text-2xl' : 'text-lg'} />
        <div className={cx('text-[10px] font-medium uppercase tracking-[0.2em]', inverse ? 'text-white/60' : 'text-muted')}>Endpoint metrics</div>
      </div>
    </div>
  )
}

export { Activity }
