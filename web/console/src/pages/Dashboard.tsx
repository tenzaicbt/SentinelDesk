import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from 'react-router-dom'
import { AppWindow, ArrowUpRight, Bell, Clock, Globe, HardDrive, LayoutDashboard, MapPin, Monitor, Wifi } from 'lucide-react'
import { api } from '../lib/api'
import { useAuth } from '../lib/auth'
import { ago, appName, bytes, duration, shortDay } from '../lib/format'
import { Badge, Card, Empty, OnlineDot, PageHeader, SeverityBadge, Skeleton, Stat } from '../components/ui'
import { Legend, RankBars, S, UsageBars } from '../components/charts'
import { BranchMap, type BranchPoint, type DevicePoint } from '../components/BranchMap'
import { useTheme } from '../components/Layout'

interface DashboardData {
  kpis: { total: number; online: number; pending: number; openAlerts: number; criticalAlerts: number; todayBytesIn: number; todayBytesOut: number; yesterdayBytes: number; activeHoursToday: number }
  usage: { day: string; bytesIn: number; bytesOut: number; activeHours: number }[]
  topApps: { app: string; seconds: number; devices: number }[]
  topSites: { domain: string; category: string; hits: number }[] | null
  categories: { category: string; hits: number }[] | null
  branches: BranchPoint[]
  deviceLocations?: DevicePoint[]
  os: { name: string; count: number }[]
  versions: { name: string; count: number }[]
  recentAlerts: { id: number; title: string; severity: string; triggeredAt: string; deviceId: string; hostname: string }[]
  topConsumers: { deviceId: string; hostname: string; bytes: number }[]
}

export default function Dashboard() {
  const { can, me } = useAuth()
  const { dark } = useTheme()
  const navigate = useNavigate()
  const { data, isLoading } = useQuery({ queryKey: ['dashboard'], queryFn: () => api.get<DashboardData>('/api/dashboard'), refetchInterval: 20_000 })

  const hour = new Date().getHours()
  const greeting = hour < 12 ? 'Good morning' : hour < 17 ? 'Good afternoon' : 'Good evening'

  if (isLoading || !data) {
    return (
      <>
        <PageHeader title="Overview" icon={<LayoutDashboard className="size-5" />} />
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">{[0, 1, 2, 3].map((i) => <Skeleton key={i} className="h-32" />)}</div>
        <Skeleton className="mt-4 h-80" />
      </>
    )
  }
  const k = data.kpis
  const todayTotal = k.todayBytesIn + k.todayBytesOut
  const onlinePct = k.total ? Math.round((k.online / k.total) * 100) : 0

  return (
    <>
      <PageHeader
        title={`${greeting}, ${me?.displayName.split(' ')[0]}`}
        subtitle={`Fleet snapshot · ${new Date().toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long' })}`}
        icon={<LayoutDashboard className="size-5" />}
      />

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Stat label="Devices online" icon={<Monitor className="size-4" />}
          value={<>{k.online}<span className="text-lg text-muted"> / {k.total}</span></>}
          sub={<span className="inline-flex items-center gap-1.5"><OnlineDot online />{onlinePct}% of fleet{k.pending ? ` · ${k.pending} pending approval` : ''}</span>} />
        <Stat label="Data used today" icon={<Wifi className="size-4" />} value={bytes(todayTotal)}
          trend={k.yesterdayBytes ? { value: ((todayTotal - k.yesterdayBytes) / k.yesterdayBytes) * 100, goodWhenDown: true } : undefined}
          sub={<>vs yesterday · ↓ {bytes(k.todayBytesIn)} ↑ {bytes(k.todayBytesOut)}</>} />
        <Stat label="Active work time today" icon={<Clock className="size-4" />} value={`${k.activeHoursToday.toLocaleString()} h`}
          sub={<>across {k.online} online devices</>} />
        <Stat label="Open alerts" icon={<Bell className="size-4" />} value={k.openAlerts}
          sub={k.criticalAlerts ? <Badge tone="critical">{k.criticalAlerts} critical</Badge> : <span>none critical</span>} />
      </div>

      <div className="mt-4 grid gap-3 xl:grid-cols-3">
        <Card className="xl:col-span-2" title="Network data usage" subtitle="All devices · last 14 days" actions={<Legend items={[{ label: 'Download', color: S.down }, { label: 'Upload', color: S.up }]} />}>
          <UsageBars data={data.usage} xKey="day" xFormat={shortDay} height={260} />
        </Card>
        <Card title="Top data consumers today" subtitle="Devices by total traffic" actions={<Link to="/reports" className="text-xs text-brand-ink hover:underline">Report</Link>}>
          {data.topConsumers.length ? (
            <RankBars rows={data.topConsumers.map((c) => ({ key: c.deviceId, label: c.hostname, value: c.bytes }))} format={(v) => bytes(v)} onClick={(id) => navigate(`/devices/${id}`)} />
          ) : <Empty title="No traffic yet today" />}
        </Card>
      </div>

      <div className="mt-4 grid gap-3 xl:grid-cols-3">
        <Card className="xl:col-span-2" pad={false} title={<span className="inline-flex items-center gap-1.5"><MapPin className="size-3.5" />Branch network</span>}
          subtitle={`Large markers = branches (size = devices, ring = share online: green ≥80%, amber ≥50%, red below)${data.deviceLocations?.length ? ` · small dots = ${data.deviceLocations.length} located device${data.deviceLocations.length === 1 ? '' : 's'} (green online, grey offline)` : ''}`}>
          <div className="p-3 pt-1">
            <BranchMap branches={data.branches} devices={data.deviceLocations} dark={dark} onSelect={(id) => navigate(`/devices?orgUnitId=${id}`)} onSelectDevice={(id) => navigate(`/devices/${id}`)} />
          </div>
        </Card>
        <Card title="Branches" subtitle="Online / total devices">
          <ul className="divide-y divide-line">
            {data.branches.slice(0, 10).map((b) => (
              <li key={b.id}>
                <Link to={`/devices?orgUnitId=${b.id}`} className="flex items-center justify-between gap-3 py-2 text-sm hover:text-brand-ink">
                  <span className="truncate">{b.name}</span>
                  <span className="flex items-center gap-3 tabular text-fg-2">
                    <span className="text-xs text-muted">{bytes(b.todayBytes)}</span>
                    <span><span className="font-medium text-fg">{b.online}</span>/{b.total}</span>
                  </span>
                </Link>
              </li>
            ))}
          </ul>
        </Card>
      </div>

      <div className="mt-4 grid gap-3 lg:grid-cols-2 xl:grid-cols-3">
        <Card title={<span className="inline-flex items-center gap-1.5"><AppWindow className="size-3.5" />Top applications today</span>} subtitle="Foreground (active) time across the fleet">
          {data.topApps.length ? (
            <RankBars rows={data.topApps.map((a) => ({ key: a.app, label: appName(a.app), value: a.seconds, sub: plural(a.devices, 'PC') }))} format={duration} />
          ) : <Empty title="No application activity yet today" />}
        </Card>
        {can.web && (
          <Card title={<span className="inline-flex items-center gap-1.5"><Globe className="size-3.5" />Top websites today</span>} subtitle="By DNS lookups (domain level), background traffic excluded">
            {data.topSites?.length ? (
              <RankBars rows={data.topSites.map((s) => ({ key: s.domain, label: <span className="inline-flex min-w-0 items-center gap-2"><span className="truncate">{s.domain}</span>{s.category && s.category !== 'Uncategorized' && <Badge>{s.category}</Badge>}</span>, value: s.hits }))} format={(v) => `${v.toLocaleString()} lookups`} />
            ) : <Empty title="No web activity yet today" />}
          </Card>
        )}
        <Card title="Recent alerts" actions={<Link to="/alerts" className="inline-flex items-center gap-0.5 text-xs text-brand-ink hover:underline">All<ArrowUpRight className="size-3" /></Link>}>
          {data.recentAlerts.length ? (
            <ul className="space-y-1">
              {data.recentAlerts.map((a) => (
                <li key={a.id}>
                  <Link to={`/devices/${a.deviceId}`} className="flex items-start gap-3 rounded-lg p-2 hover:bg-panel-2">
                    <span className="flex w-[4.75rem] shrink-0 justify-start pt-px"><SeverityBadge severity={a.severity} /></span>
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-sm font-medium">{a.title}</span>
                      <span className="block text-xs text-muted">{a.hostname} · {ago(a.triggeredAt)}</span>
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          ) : <Empty icon={<Bell className="size-5" />} title="All clear">No open alerts.</Empty>}
        </Card>
      </div>

      <div className="mt-4 grid gap-3 lg:grid-cols-2 xl:grid-cols-3">
        {can.web && data.categories && data.categories.length > 0 && (
          <Card className="xl:col-span-2" title="Web categories today" subtitle="Share of DNS lookups by category">
            <CategoryShare rows={data.categories} />
          </Card>
        )}
        <Card title={<span className="inline-flex items-center gap-1.5"><HardDrive className="size-3.5" />Fleet software</span>} subtitle="Plan staged rollouts in Agent management">
          <ShareList heading="Operating systems" rows={data.os.map((o) => ({ key: o.name, label: o.name || 'Unknown', value: o.count }))} />
          <div className="mt-4"><ShareList heading="Agent versions" rows={data.versions.map((o) => ({ key: o.name, label: o.name ? `v${o.name}` : 'Unknown', value: o.count }))} /></div>
        </Card>
      </div>
    </>
  )
}

const plural = (n: number, word: string) => `${n.toLocaleString()} ${word}${n === 1 ? '' : 's'}`

/** Categorical palette for shares; "Uncategorized" and the remainder stay neutral so real categories stand out. */
const SHARE_COLORS = ['var(--series-1)', 'var(--series-2)', 'var(--series-3)', 'var(--series-4)', 'var(--series-5)', 'var(--series-7)']
const NEUTRAL = 'var(--axis)'

function CategoryShare({ rows }: { rows: { category: string; hits: number }[] }) {
  const total = rows.reduce((t, r) => t + r.hits, 0) || 1
  const named = rows.filter((r) => r.category !== 'Uncategorized')
  const top = named.slice(0, SHARE_COLORS.length).map((r, i) => ({ label: r.category, value: r.hits, color: SHARE_COLORS[i] }))
  const rest = named.slice(SHARE_COLORS.length).reduce((t, r) => t + r.hits, 0)
  const unc = rows.find((r) => r.category === 'Uncategorized')?.hits ?? 0
  const parts = [...top, ...(rest ? [{ label: 'Other', value: rest, color: NEUTRAL }] : []), ...(unc ? [{ label: 'Uncategorized', value: unc, color: NEUTRAL }] : [])]
  const pct = (v: number) => `${Math.round((v / total) * 100)}%`
  return (
    <>
      <div className="flex h-2.5 gap-0.5 overflow-hidden rounded-full" role="img" aria-label="Share of lookups by category">
        {parts.map((p) => (
          <div key={p.label} title={`${p.label}: ${p.value.toLocaleString()} (${pct(p.value)})`} className="h-full first:rounded-l-full last:rounded-r-full"
            style={{ width: `${(p.value / total) * 100}%`, background: p.color, opacity: p.label === 'Uncategorized' ? 0.45 : 1 }} />
        ))}
      </div>
      <ul className="mt-3 grid gap-x-6 gap-y-2 sm:grid-cols-2">
        {parts.map((p) => (
          <li key={p.label} className="flex items-center gap-2 text-[13px]">
            <span className="inline-block size-2.5 shrink-0 rounded-[3px]" style={{ background: p.color, opacity: p.label === 'Uncategorized' ? 0.45 : 1 }} />
            <span className="min-w-0 flex-1 truncate text-fg">{p.label}</span>
            <span className="tabular text-fg-2">{p.value.toLocaleString()}</span>
            <span className="w-10 text-right tabular text-muted">{pct(p.value)}</span>
          </li>
        ))}
      </ul>
    </>
  )
}

function ShareList({ heading, rows }: { heading: string; rows: { key: string; label: string; value: number }[] }) {
  const total = rows.reduce((t, r) => t + r.value, 0) || 1
  return (
    <section>
      <h3 className="mb-2 text-xs font-medium text-muted">{heading}</h3>
      {rows.length ? (
        <ul className="space-y-2">
          {rows.map((r) => (
            <li key={r.key} className="text-[13px]">
              <div className="mb-1 flex items-baseline justify-between gap-3">
                <span className="min-w-0 truncate text-fg">{r.label}</span>
                <span className="shrink-0 tabular text-fg-2">{plural(r.value, 'device')}<span className="ml-1.5 text-muted">{Math.round((r.value / total) * 100)}%</span></span>
              </div>
              <div className="h-1.5 rounded-full bg-panel-2"><div className="h-full rounded-full" style={{ width: `${Math.max(2, (r.value / total) * 100)}%`, background: 'var(--series-1)' }} /></div>
            </li>
          ))}
        </ul>
      ) : <p className="text-xs text-muted">No data yet</p>}
    </section>
  )
}
