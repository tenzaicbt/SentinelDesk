import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { AppWindow, Cpu, Download, FileBarChart2, Globe, Package, Wifi } from 'lucide-react'
import { api, download, qs } from '../lib/api'
import { useAuth } from '../lib/auth'
import { appName, daysAgo, num, shortDay, today } from '../lib/format'
import { Badge, Button, Card, Empty, Field, Input, PageHeader, Select, Skeleton, Table, Tabs } from '../components/ui'
import { Legend, RankBars, S, UsageBars } from '../components/charts'
import type { OrgUnit } from './Devices'

type Kind = 'usage' | 'apps' | 'web' | 'inventory' | 'software'

export default function Reports() {
  const { can } = useAuth()
  const [kind, setKind] = useState<Kind>('usage')
  const [from, setFrom] = useState(daysAgo(6))
  const [to, setTo] = useState(today())
  const [orgUnitId, setOrg] = useState('')
  const { data: org } = useQuery({ queryKey: ['org'], queryFn: () => api.get<OrgUnit[]>('/api/org') })
  const params = { from: kind === 'inventory' || kind === 'software' ? undefined : from, to: kind === 'inventory' || kind === 'software' ? undefined : to, orgUnitId }
  const { data, isFetching } = useQuery({ queryKey: ['report', kind, params], queryFn: () => api.get<Record<string, unknown>>(`/api/reports/${kind}${qs(params)}`) })

  const exportCsv = async () => {
    try { await download(`/api/reports/${kind}${qs({ ...params, format: 'csv' })}`); toast.success('Export downloaded (opens in Excel)') }
    catch { toast.error('Export failed') }
  }
  const presets = [{ l: 'Today', f: today() }, { l: '7 days', f: daysAgo(6) }, { l: '30 days', f: daysAgo(29) }, { l: '90 days', f: daysAgo(89) }]
  const dated = kind !== 'inventory' && kind !== 'software'

  return (
    <>
      <PageHeader title="Reports" icon={<FileBarChart2 className="size-5" />} subtitle="Every export is recorded in the audit log"
        actions={<Button variant="primary" icon={<Download className="size-4" />} onClick={exportCsv}>Export CSV</Button>} />
      <Tabs value={kind} onChange={setKind} tabs={[
        { id: 'usage', label: 'Data usage', icon: <Wifi className="size-3.5" /> },
        { id: 'apps', label: 'Application usage', icon: <AppWindow className="size-3.5" /> },
        ...(can.web ? [{ id: 'web' as Kind, label: 'Website usage', icon: <Globe className="size-3.5" /> }] : []),
        { id: 'inventory', label: 'Hardware inventory', icon: <Cpu className="size-3.5" /> },
        { id: 'software', label: 'Software inventory', icon: <Package className="size-3.5" /> },
      ]} />

      <div className="my-4 flex flex-wrap items-end gap-3">
        {dated && (
          <>
            <Field label="From"><Input type="date" value={from} max={to} onChange={(e) => setFrom(e.target.value)} /></Field>
            <Field label="To"><Input type="date" value={to} min={from} max={today()} onChange={(e) => setTo(e.target.value)} /></Field>
            <div className="flex gap-1 pb-0.5">
              {presets.map((p) => <Button key={p.l} size="sm" variant={from === p.f && to === today() ? 'primary' : 'ghost'} onClick={() => { setFrom(p.f); setTo(today()) }}>{p.l}</Button>)}
            </div>
          </>
        )}
        <Field label="Scope">
          <Select value={orgUnitId} onChange={(e) => setOrg(e.target.value)}>
            <option value="">Whole organization</option>
            {org?.filter((o) => o.type !== 'organization').sort((a, b) => a.name.localeCompare(b.name)).map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
          </Select>
        </Field>
        {isFetching && <span className="pb-2 text-xs text-muted">Updating…</span>}
      </div>

      {!data ? <Skeleton className="h-96" /> : (
        <>
          {kind === 'usage' && <UsageReport data={data as unknown as UsageData} />}
          {kind === 'apps' && <AppsReport rows={(data as { rows: AppRow[] }).rows} />}
          {kind === 'web' && <WebReport data={data as unknown as WebData} />}
          {kind === 'inventory' && <InventoryReport rows={(data as { rows: InvRow[] }).rows} />}
          {kind === 'software' && <SoftwareReport rows={(data as { rows: SwRow[] }).rows} />}
        </>
      )}
    </>
  )
}

interface UsageData { rows: { hostname: string; branch?: string; user?: string; inMb: number; outMb: number; totalMb: number; activeHours: number; idleHours: number; deviceId: string }[]; series: { day: string; bytesIn: number; bytesOut: number }[]; byBranch: { branch: string; totalMb: number; devices: number }[] }
function UsageReport({ data }: { data: UsageData }) {
  const total = data.rows.reduce((s, r) => s + r.totalMb, 0)
  const mb = (v: number) => (v >= 1024 ? `${(v / 1024).toFixed(2)} GB` : `${v.toFixed(1)} MB`)
  return (
    <div className="grid gap-3 xl:grid-cols-3">
      <Card className="xl:col-span-2" title="Daily totals" subtitle={`${mb(total)} across ${data.rows.length} devices`} actions={<Legend items={[{ label: 'Download', color: S.down }, { label: 'Upload', color: S.up }]} />}>
        <UsageBars data={data.series} xKey="day" xFormat={shortDay} />
      </Card>
      <Card title="By branch">
        <RankBars rows={data.byBranch.slice(0, 12).map((b) => ({ key: b.branch, label: b.branch, value: b.totalMb, sub: `${b.devices} PCs` }))} format={mb} />
      </Card>
      <Card className="xl:col-span-3" pad={false} title="Per device">
        {data.rows.length === 0 ? <Empty title="No data in range" /> : (
          <Table>
            <thead><tr><th>Device</th><th className="hidden md:table-cell">Branch</th><th className="hidden md:table-cell">User</th><th className="text-right">Download</th><th className="text-right">Upload</th><th className="text-right">Total</th><th className="text-right">Active h</th><th className="text-right hidden sm:table-cell">Idle h</th></tr></thead>
            <tbody>
              {data.rows.map((r) => (
                <tr key={r.deviceId}>
                  <td><Link to={`/devices/${r.deviceId}`} className="font-medium hover:text-brand-ink">{r.hostname}</Link></td>
                  <td className="hidden text-fg-2 md:table-cell">{r.branch}</td>
                  <td className="hidden text-fg-2 md:table-cell">{r.user?.split('\\').pop()}</td>
                  <td className="text-right tabular">{mb(r.inMb)}</td><td className="text-right tabular">{mb(r.outMb)}</td>
                  <td className="text-right font-medium tabular">{mb(r.totalMb)}</td>
                  <td className="text-right tabular">{r.activeHours}</td><td className="hidden text-right tabular sm:table-cell">{r.idleHours}</td>
                </tr>
              ))}
            </tbody>
          </Table>
        )}
      </Card>
    </div>
  )
}

interface AppRow { app: string; hours: number; devices: number; avgHoursPerDevice: number }
function AppsReport({ rows }: { rows: AppRow[] }) {
  return (
    <div className="grid gap-3 xl:grid-cols-3">
      <Card title="Top applications" subtitle="Total active hours"><RankBars rows={rows.slice(0, 12).map((r) => ({ key: r.app, label: appName(r.app), value: r.hours }))} format={(v) => `${v} h`} /></Card>
      <Card className="xl:col-span-2" pad={false} title="All applications">
        <Table>
          <thead><tr><th>Application</th><th className="text-right">Active hours</th><th className="text-right">Devices</th><th className="text-right">Avg h / device</th></tr></thead>
          <tbody>{rows.map((r) => <tr key={r.app}><td><span className="font-medium">{appName(r.app)}</span> <span className="font-mono text-[11px] text-muted">{r.app}</span></td><td className="text-right tabular">{r.hours}</td><td className="text-right tabular">{r.devices}</td><td className="text-right tabular">{r.avgHoursPerDevice}</td></tr>)}</tbody>
        </Table>
      </Card>
    </div>
  )
}

interface WebData { rows: { domain: string; category: string; hits: number; devices: number }[]; categories: { category: string; hits: number }[] }
function WebReport({ data }: { data: WebData }) {
  return (
    <div className="grid gap-3 xl:grid-cols-3">
      <Card title="Categories"><RankBars color="var(--series-7)" rows={data.categories.map((c) => ({ key: c.category, label: c.category, value: c.hits }))} format={num} /></Card>
      <Card className="xl:col-span-2" pad={false} title="Domains">
        <Table>
          <thead><tr><th>Domain</th><th>Category</th><th className="text-right">Lookups</th><th className="text-right">Devices</th></tr></thead>
          <tbody>{data.rows.slice(0, 300).map((r) => <tr key={r.domain}><td className="font-medium">{r.domain}</td><td><Badge>{r.category}</Badge></td><td className="text-right tabular">{num(r.hits)}</td><td className="text-right tabular">{r.devices}</td></tr>)}</tbody>
        </Table>
      </Card>
    </div>
  )
}

interface InvRow { hostname: string; branch?: string; user?: string; manufacturer?: string; model?: string; serial?: string; os?: string; osBuild?: string; cpu?: string; ramGb?: number; diskGb?: number; agentVersion: string; antivirus?: string; avEnabled?: boolean; firewall?: boolean; bitLocker?: string; lastPatch?: string }
function InventoryReport({ rows }: { rows: InvRow[] }) {
  const yes = (v?: boolean) => v === undefined || v === null ? '—' : v ? <Badge tone="good">Yes</Badge> : <Badge tone="critical">No</Badge>
  return (
    <Card pad={false} title={`${rows.length} devices`}>
      <Table>
        <thead><tr><th>Device</th><th>Model</th><th>Serial</th><th>OS</th><th className="text-right">RAM</th><th className="text-right">Disk</th><th>AV</th><th>Firewall</th><th>BitLocker</th><th>Last patch</th></tr></thead>
        <tbody>{rows.map((r) => (
          <tr key={r.hostname}>
            <td><div className="font-medium">{r.hostname}</div><div className="text-xs text-muted">{r.branch}</div></td>
            <td className="text-xs">{r.manufacturer} {r.model}</td><td className="font-mono text-xs">{r.serial}</td>
            <td className="text-xs">{r.os}<div className="text-muted">{r.osBuild}</div></td>
            <td className="text-right tabular">{r.ramGb ?? '—'} GB</td><td className="text-right tabular">{r.diskGb ?? '—'} GB</td>
            <td>{yes(r.avEnabled)}</td><td>{yes(r.firewall)}</td>
            <td>{r.bitLocker ? <Badge tone={r.bitLocker === 'On' ? 'good' : 'warning'}>{r.bitLocker}</Badge> : '—'}</td>
            <td className="text-xs text-muted">{r.lastPatch ?? '—'}</td>
          </tr>
        ))}</tbody>
      </Table>
    </Card>
  )
}

interface SwRow { name: string; publisher?: string; installs: number; versions: string }
function SoftwareReport({ rows }: { rows: SwRow[] }) {
  const [q, setQ] = useState('')
  const r = rows.filter((x) => !q || x.name.toLowerCase().includes(q.toLowerCase()))
  return (
    <Card pad={false} title={`${rows.length} distinct titles`} actions={<Input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search…" className="w-56" />}>
      <Table>
        <thead><tr><th>Software</th><th className="hidden md:table-cell">Publisher</th><th className="text-right">Installs</th><th>Versions</th></tr></thead>
        <tbody>{r.slice(0, 500).map((x) => <tr key={x.name}><td className="font-medium">{x.name}</td><td className="hidden text-fg-2 md:table-cell">{x.publisher}</td><td className="text-right tabular">{x.installs}</td><td className="max-w-64 truncate font-mono text-xs text-muted">{x.versions}</td></tr>)}</tbody>
      </Table>
    </Card>
  )
}
