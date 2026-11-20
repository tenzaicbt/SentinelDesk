import { useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ArrowDown, ArrowUp, Lock, Moon, Radio, Search, Wifi } from 'lucide-react'
import { api } from '../lib/api'
import { useHub, useHubEvent, useHubGroup, type Presence } from '../lib/hub'
import { appName, bps, bytes } from '../lib/format'
import { Badge, Card, cx, Empty, Input, Meter, OnlineDot, PageHeader, Segmented } from '../components/ui'
import { Sparkline } from '../components/charts'

interface LiveRow { presence: Presence; branch?: string }

export default function Live() {
  const { connected } = useHub()
  const { data } = useQuery({ queryKey: ['live'], queryFn: () => api.get<LiveRow[]>('/api/live') })
  const [map, setMap] = useState<Record<string, { p: Presence; branch?: string; hist: number[] }>>({})
  const [q, setQ] = useState('')
  const [sort, setSort] = useState<'name' | 'net' | 'cpu'>('net')
  const [, tick] = useState(0)

  useEffect(() => {
    if (!data) return
    setMap((m) => {
      const n = { ...m }
      data.forEach((r) => { n[r.presence.deviceId] = { p: r.presence, branch: r.branch, hist: m[r.presence.deviceId]?.hist ?? [] } })
      return n
    })
  }, [data])

  useHubGroup('WatchLive', 'UnwatchLive')
  useHubEvent<Presence>('presence', (p) => setMap((m) => ({
    ...m,
    [p.deviceId]: { p, branch: m[p.deviceId]?.branch, hist: [...(m[p.deviceId]?.hist ?? []), p.netInBps + p.netOutBps].slice(-30) },
  })))

  // age out devices that stop reporting
  useEffect(() => { const t = setInterval(() => tick((x) => x + 1), 5000); return () => clearInterval(t) }, [])

  const rows = useMemo(() => {
    const now = Date.now()
    let r = Object.values(map).filter((x) => now - new Date(x.p.lastSeen).getTime() < 150_000)
    const s = q.toLowerCase()
    if (s) r = r.filter((x) => [x.p.hostname, x.p.user, x.branch, x.p.currentApp].some((v) => v?.toLowerCase().includes(s)))
    return r.sort((a, b) =>
      sort === 'name' ? a.p.hostname.localeCompare(b.p.hostname)
        : sort === 'cpu' ? b.p.cpu - a.p.cpu
          : b.p.netInBps + b.p.netOutBps - (a.p.netInBps + a.p.netOutBps))
  }, [map, q, sort])

  const totalIn = rows.reduce((s, r) => s + r.p.netInBps, 0)
  const totalOut = rows.reduce((s, r) => s + r.p.netOutBps, 0)
  const active = rows.filter((r) => !r.p.idle && !r.p.locked).length

  return (
    <>
      <PageHeader title="Live view" icon={<Radio className="size-5" />}
        subtitle={<span className="inline-flex items-center gap-2"><OnlineDot online={connected} />{connected ? 'Streaming heartbeats in real time' : 'Reconnecting…'}</span>} />

      <div className="mb-4 grid gap-4 sm:grid-cols-3">
        <Card><p className="text-sm text-muted">Online now</p><p className="mt-1 text-2xl font-semibold tabular">{rows.length}</p><p className="text-xs text-muted">{active} actively in use</p></Card>
        <Card><p className="text-sm text-muted">Fleet download</p><p className="mt-1 text-2xl font-semibold tabular text-fg">{bps(totalIn)}</p><p className="text-xs text-muted">sum of current rates</p></Card>
        <Card><p className="text-sm text-muted">Fleet upload</p><p className="mt-1 text-2xl font-semibold tabular">{bps(totalOut)}</p><p className="text-xs text-muted">sum of current rates</p></Card>
      </div>

      <div className="mb-4 flex flex-wrap items-center gap-2">
        <div className="relative max-w-sm flex-1">
          <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted" />
          <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Filter devices…" className="pl-9" />
        </div>
        <Segmented value={sort} onChange={setSort} options={[{ value: 'net', label: 'Bandwidth' }, { value: 'cpu', label: 'CPU' }, { value: 'name', label: 'Name' }]} />
      </div>

      {rows.length === 0 ? (
        <Card><Empty icon={<Wifi className="size-5" />} title="No devices online">Devices appear here as soon as their agent sends a heartbeat.</Empty></Card>
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3 2xl:grid-cols-4">
          {rows.map(({ p, branch, hist }) => (
            <Link key={p.deviceId} to={`/devices/${p.deviceId}`}
              className="group rounded-xl border border-line bg-panel p-4 shadow-card transition-colors hover:bg-panel-2/50">
              <div className="flex items-start justify-between gap-2">
                <div className="min-w-0">
                  <div className="flex items-center gap-2 font-medium group-hover:underline"><OnlineDot online />{p.hostname}</div>
                  <div className="truncate text-xs text-muted">{p.user?.split('\\').pop()} · {branch ?? 'Unassigned'}</div>
                </div>
                {p.locked ? <Badge icon={<Lock className="size-3" />}>Locked</Badge> : p.idle ? <Badge icon={<Moon className="size-3" />}>Idle</Badge> : p.connectionType ? <Badge>{p.connectionType}</Badge> : null}
              </div>
              <div className={cx('mt-3 truncate rounded-md bg-panel-2 px-2.5 py-1.5 text-xs', p.currentApp ? 'text-fg' : 'text-muted')}
                title={p.currentTitle ?? undefined}>
                {p.currentApp ? <><span className="font-medium">{appName(p.currentApp)}</span>{p.currentTitle && <span className="text-muted"> — {p.currentTitle}</span>}</> : 'No foreground activity'}
              </div>
              <div className="mt-3 flex items-end justify-between gap-3">
                <div className="space-y-0.5 text-xs tabular">
                  <div className="flex items-center gap-1 text-fg-2"><ArrowDown className="size-3 text-s1" />{bps(p.netInBps)}</div>
                  <div className="flex items-center gap-1 text-fg-2"><ArrowUp className="size-3 text-s2" />{bps(p.netOutBps)}</div>
                </div>
                <div className="w-28"><Sparkline values={hist} /></div>
              </div>
              <div className="mt-3 grid grid-cols-2 gap-3">
                <Meter label="CPU" value={p.cpu} />
                <Meter label="RAM" value={p.ram} />
              </div>
              <div className="mt-2 text-[11px] text-muted">Today {bytes(p.todayIn + p.todayOut)}</div>
            </Link>
          ))}
        </div>
      )}
    </>
  )
}
