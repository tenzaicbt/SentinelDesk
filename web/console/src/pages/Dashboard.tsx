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
            <RankBars rows={data.topApps.map((a) => ({ key: a.app, label: appName(a.app), value: a.seconds, sub: `${a.devices} PCs` }))} format={duration} />
          ) : <Empty title="No application activity yet today" />}
        </Card>
        {can.web && (
          <Card title={<span className="inline-flex items-center gap-1.5"><Globe className="size-3.5" />Top websites today</span>} subtitle="By DNS lookups (domain level), background traffic excluded">
            {data.topSites?.length ? (
              <RankBars color="var(--series-7)" rows={data.topSites.map((s) => ({ key: s.domain, label: <span className="inline-flex items-center gap-2">{s.domain}<Badge>{s.category}</Badge></span>, value: s.hits }))} format={(v) => v.toLocaleString()} />
            ) : <Empty title="No web activity yet today" />}
          </Card>
        )}
        <Card title="Recent alerts" actions={<Link to="/alerts" className="inline-flex items-center gap-0.5 text-xs text-brand-ink hover:underline">All<ArrowUpRight className="size-3" /></Link>}>
          {data.recentAlerts.length ? (
            <ul className="space-y-1">
              {data.recentAlerts.map((a) => (
                <li key={a.id}>
                  <Link to={`/devices/${a.deviceId}`} className="flex items-start gap-3 rounded-lg p-2 hover:bg-panel-2">
                    <SeverityBadge severity={a.severity} />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-sm">{a.title}</span>
                      <span className="block text-xs text-muted">{a.hostname} · {ago(a.triggeredAt)}</span>
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          ) : <Empty icon={<Bell className="size-5" />} title="All clear">No open alerts.</Empty>}
        </Card>
        {can.web && data.categories && data.categories.length > 0 && (
          <Card title="Web categories today" subtitle="Share of lookups by category">
            <RankBars color="var(--series-3)" rows={data.categories.slice(0, 8).map((c) => ({ key: c.category, label: c.category, value: c.hits }))} format={(v) => v.toLocaleString()} />
          </Card>
        )}
        <Card title={<span className="inline-flex items-center gap-1.5"><HardDrive className="size-3.5" />Operating systems</span>}>
          <RankBars color="var(--series-1)" rows={data.os.map((o) => ({ key: o.name, label: o.name, value: o.count }))} format={(v) => `${v} devices`} />
        </Card>
        <Card title="Agent versions" subtitle="Plan staged rollouts in Agent management">
          <RankBars color="var(--series-1)" rows={data.versions.map((o) => ({ key: o.name, label: `v${o.name || 'unknown'}`, value: o.count }))} format={(v) => `${v} devices`} />
        </Card>
      </div>
    </>
  )
}
