import { useEffect, useMemo, useState } from 'react'
import { createPortal } from 'react-dom'
import { useNavigate } from 'react-router-dom'
import { Bell, Building2, CornerDownLeft, FileBarChart2, KeyRound, LayoutDashboard, Monitor, Radio, Search, SlidersHorizontal } from 'lucide-react'
import { api } from '../lib/api'
import { cx } from './ui'

interface Hit { id: string; hostname: string; loggedInUser?: string; lastIp?: string; status: string }

const pages = [
  { label: 'Overview', to: '/', icon: LayoutDashboard },
  { label: 'Live view', to: '/live', icon: Radio },
  { label: 'Devices', to: '/devices', icon: Monitor },
  { label: 'Alerts', to: '/alerts', icon: Bell },
  { label: 'Reports', to: '/reports', icon: FileBarChart2 },
  { label: 'Policies', to: '/policies', icon: SlidersHorizontal },
  { label: 'Organization', to: '/organization', icon: Building2 },
  { label: 'Agent management', to: '/agents', icon: KeyRound },
]

export function CommandPalette({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [q, setQ] = useState('')
  const [hits, setHits] = useState<Hit[]>([])
  const [idx, setIdx] = useState(0)
  const navigate = useNavigate()

  useEffect(() => { if (open) { setQ(''); setHits([]); setIdx(0) } }, [open])
  useEffect(() => {
    if (q.trim().length < 2) { setHits([]); return }
    const t = setTimeout(() => api.get<Hit[]>(`/api/search?q=${encodeURIComponent(q)}`).then(setHits).catch(() => setHits([])), 150)
    return () => clearTimeout(t)
  }, [q])

  const items = useMemo(() => {
    const p = pages.filter((x) => x.label.toLowerCase().includes(q.toLowerCase())).map((x) => ({ key: x.to, label: x.label, sub: 'Page', to: x.to, icon: <x.icon className="size-4" /> }))
    const d = hits.map((h) => ({ key: h.id, label: h.hostname, sub: [h.loggedInUser, h.lastIp].filter(Boolean).join(' · '), to: `/devices/${h.id}`, icon: <Monitor className="size-4" /> }))
    return [...d, ...p]
  }, [hits, q])

  if (!open) return null
  const go = (to: string) => { navigate(to); onClose() }

  return createPortal(
    <div className="fixed inset-0 z-50 flex items-start justify-center bg-black/50 p-4 pt-[12vh]" onMouseDown={onClose}>
      <div className="w-full max-w-xl overflow-hidden rounded-xl border border-line bg-panel shadow-lg animate-rise" onMouseDown={(e) => e.stopPropagation()}>
        <div className="flex items-center gap-2 border-b border-line px-3">
          <Search className="size-4 shrink-0 opacity-50" />
          <input
            autoFocus
            value={q}
            onChange={(e) => { setQ(e.target.value); setIdx(0) }}
            onKeyDown={(e) => {
              if (e.key === 'Escape') onClose()
              if (e.key === 'ArrowDown') { e.preventDefault(); setIdx((i) => Math.min(items.length - 1, i + 1)) }
              if (e.key === 'ArrowUp') { e.preventDefault(); setIdx((i) => Math.max(0, i - 1)) }
              if (e.key === 'Enter' && items[idx]) go(items[idx].to)
            }}
            placeholder="Hostname, user, serial, IP or page…"
            className="h-11 flex-1 bg-transparent text-sm outline-none placeholder:text-muted"
          />
          <kbd className="rounded border border-line bg-panel-2 px-1.5 font-mono text-[10px] font-medium text-muted">ESC</kbd>
        </div>
        <ul className="max-h-[50vh] overflow-y-auto p-1">
          {items.length === 0 && <li className="py-6 text-center text-sm text-muted">No results found.</li>}
          {items.map((it, i) => (
            <li key={it.key}>
              <button
                onMouseEnter={() => setIdx(i)}
                onClick={() => go(it.to)}
                className={cx('flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-left text-sm [&_svg]:text-muted', i === idx && 'bg-panel-2')}
              >
                <span>{it.icon}</span>
                <span className="min-w-0 flex-1">
                  <span className="block truncate">{it.label}</span>
                  {it.sub && <span className="block truncate text-xs text-muted">{it.sub}</span>}
                </span>
                {i === idx && <CornerDownLeft className="size-3.5" />}
              </button>
            </li>
          ))}
        </ul>
      </div>
    </div>,
    document.body,
  )
}
