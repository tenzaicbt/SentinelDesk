import { useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { toast } from 'sonner'
import {
  Activity, AppWindow, ArrowDown, ArrowLeft, ArrowUp, Ban, CheckCircle2, ChevronRight, Globe, KeyRound, Laptop, ListTree,
  Cpu, MemoryStick, MessageSquare, Monitor, MoreHorizontal, Network, Package, RefreshCw, Trash2, Wifi, XCircle,
} from 'lucide-react'
import { api, ApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { useHubEvent, useHubGroup, type LiveSample, type Presence } from '../lib/hub'
import { ago, appName, bps, bytes, dateTime, duration, num, shortDay } from '../lib/format'
import {
  Badge, Button, Callout, Card, cx, Empty, Field, Input, Kv, Modal, OnlineDot, Segmented, Select, Skeleton,
  StatusPill, Table, Tabs,
} from '../components/ui'
import { ActivityBars, Legend, Lines, RankBars, S, Sparkline, UsageBars } from '../components/charts'
import { MessageModal, type OrgUnit } from './Devices'

interface HardwareInfo {
  manufacturer?: string; model?: string; serial?: string; biosVersion?: string; cpu?: string; cpuCores: number; cpuThreads: number; ramMb: number
  os?: string; osBuild?: string; domain?: string; gpu?: string; tpmPresent: boolean
  disks: { name: string; model?: string; sizeGb: number; freeGb: number; type?: string }[]
  nics: { name: string; mac?: string; type?: string; ip?: string; speedMbps: number }[]
}
interface SecurityPosture { antivirus?: string; antivirusEnabled?: boolean; antivirusUpToDate?: boolean; firewallEnabled?: boolean; bitLocker?: string; lastUpdateInstalled?: string; rebootPending?: boolean; usbStorageBlocked?: boolean }
interface Device {
  id: string; hostname: string; domain?: string; machineId: string; serial?: string; manufacturer?: string; model?: string; os: string; osBuild: string
  agentVersion: string; status: string; enrolledAt: string; lastSeenAt?: string; lastIp?: string; publicIp?: string; loggedInUser?: string
  orgUnitId?: string; orgPath: string[]; tags?: string; certThumbprint?: string; certExpiresAt?: string; isSimulated: boolean
  policy: { id: string; name: string; effectiveVersion: number; applied: boolean }
  online: boolean; presence?: Presence; today: { bytesIn: number; bytesOut: number; activeSec: number; idleSec: number }
  hardware?: HardwareInfo; hardwareCollectedAt?: string; security?: SecurityPosture; securityCollectedAt?: string; liveChannel: boolean
}

type Tab = 'live' | 'bandwidth' | 'web' | 'network'

export default function DeviceDetail() {
  const { id } = useParams<{ id: string }>()
  const { can } = useAuth()
  const qc = useQueryClient()
  const [tab, setTab] = useState<Tab>('live')
  const { data: d, isLoading, error } = useQuery({ queryKey: ['device', id], queryFn: () => api.get<Device>(`/api/devices/${id}`), refetchInterval: 60_000 })
  const [presence, setPresence] = useState<Presence | undefined>()
  const [netHist, setNetHist] = useState<NetHist>([])
  const [stream, setStream] = useState<StreamState | undefined>()
  const lastSampleAt = useRef(0)

  useEffect(() => { setPresence(d?.presence) }, [d?.presence])
  useHubGroup('WatchDevice', 'UnwatchDevice', id)
  const streaming = (now = Date.now()) => now - lastSampleAt.current < 3_000
  useHubEvent<Presence>('presence', (p) => {
    if (p.deviceId !== id) return
    // while the sub-second stream runs, keep its fresher live fields and don't mix heartbeat points into the history
    if (streaming()) {
      setPresence((cur) => cur ? {
        ...p, cpu: cur.cpu, ram: cur.ram, netInBps: cur.netInBps, netOutBps: cur.netOutBps, currentApp: cur.currentApp,
        currentTitle: cur.currentTitle, idle: cur.idle, locked: cur.locked, lastSeen: cur.lastSeen,
      } : p)
      return
    }
    setPresence(p)
    setNetHist((h) => [...h, { t: new Date().toLocaleTimeString(), in: p.netInBps, out: p.netOutBps, cpu: p.cpu, ram: p.ram }].slice(-HIST))
  })
  // agent -> server -> console relay while this page is open: one sample every ~500 ms, milliseconds after it was taken
  useHubEvent<LiveSample>('liveSample', (s) => {
    if (s.deviceId !== id) return
    const now = Date.now()
    if (!streaming(now)) setNetHist([]) // switching from heartbeat cadence: start a clean sub-second history
    lastSampleAt.current = now
    setStream({ intervalMs: s.intervalMs, lastAt: now })
    setPresence((cur) => cur && {
      ...cur, cpu: s.cpu, ram: s.ram, netInBps: s.netInBps, netOutBps: s.netOutBps, currentApp: s.currentApp,
      currentTitle: s.currentTitle, idle: s.idle, locked: s.locked, lastSeen: new Date(s.serverTimeMs).toISOString(),
    })
    setNetHist((h) => [...h, { t: new Date(s.agentTimeMs).toLocaleTimeString(), in: s.netInBps, out: s.netOutBps, cpu: s.cpu, ram: s.ram }].slice(-HIST))
  })
  // flag the stream as stopped if samples dry up (agent disconnected, device asleep)
  useEffect(() => {
    if (!stream) return
    const t = setTimeout(() => setStream((s) => (s && Date.now() - s.lastAt >= 3_000 ? undefined : s)), 3_100)
    return () => clearTimeout(t)
  }, [stream])
  useHubEvent<string>('inventoryUpdated', () => qc.invalidateQueries({ queryKey: ['device', id] }))
  useHubEvent<{ type: string; status: string; result?: string }>('commandStatus', (c) => {
    toast[c.status === 'succeeded' ? 'success' : 'error'](`${c.type}: ${c.status}`, { description: c.result })
    qc.invalidateQueries({ queryKey: ['commands', id] })
  })

  if (error) return <Callout tone="critical">{(error as Error).message}</Callout>
  if (isLoading || !d) return <div className="space-y-4"><Skeleton className="h-24" /><Skeleton className="h-96" /></div>

  const online = !!presence && Date.now() - new Date(presence.lastSeen).getTime() < 150_000
  const laptop = d.tags?.includes('laptop') || /book|thinkpad|latitude|notebook/i.test(d.model ?? '')

  const tabs: { id: Tab; label: string; icon: React.ReactNode }[] = [
    { id: 'live', label: 'Live activity', icon: <Activity className="size-3.5" /> },
    { id: 'bandwidth', label: 'Internet usage', icon: <ArrowDown className="size-3.5" /> },
    ...(can.web ? [{ id: 'web' as Tab, label: 'Browsing', icon: <Globe className="size-3.5" /> }] : []),
    { id: 'network', label: 'Network', icon: <Network className="size-3.5" /> },
  ]

  return (
    <>
      <nav className="mb-3 flex items-center gap-1 text-xs text-muted">
        <Link to="/devices" className="inline-flex items-center gap-1 hover:text-fg"><ArrowLeft className="size-3.5" />Devices</Link>
        {d.orgPath.map((p) => <span key={p} className="inline-flex items-center gap-1"><ChevronRight className="size-3" />{p}</span>)}
      </nav>

      <div className="mb-5 flex flex-wrap items-start justify-between gap-4">
        <div className="flex items-center gap-4">
          <div className="relative grid size-12 place-items-center rounded-lg border border-line bg-panel-2 text-fg">
            {laptop ? <Laptop className="size-6" /> : <Monitor className="size-6" />}
            <OnlineDot online={online} className="absolute -right-1 -top-1 size-3 ring-2 ring-panel dark:ring-page" />
          </div>
          <div>
            <h1 className="flex flex-wrap items-center gap-2 text-2xl font-semibold tracking-tight">
              {d.hostname}
              <StatusPill online={online} />
              {d.status !== 'active' && <Badge tone="warning">{d.status}</Badge>}
              {d.isSimulated && <Badge tone="brand">demo</Badge>}
            </h1>
            <p className="mt-1 text-sm text-muted">
              {[d.manufacturer, d.model].filter(Boolean).join(' ')} · {d.os} ({d.osBuild}) · {online ? `${presence?.user ?? d.loggedInUser ?? 'no user'}` : `last seen ${ago(d.lastSeenAt)}`}
            </p>
          </div>
        </div>
        {can.write && <DeviceActions d={d} online={online} />}
      </div>

      {d.status === 'pending' && can.write && (
        <div className="mb-4"><Callout tone="warning">This device enrolled and is awaiting approval. Telemetry is buffered on the device until it is approved.</Callout></div>
      )}

      <Tabs tabs={tabs} value={tab} onChange={setTab} />
      <div className="mt-5">
        {tab === 'live' && <LiveActivity d={d} p={online ? presence : undefined} netHist={netHist} stream={online ? stream : undefined} />}
        {tab === 'bandwidth' && <Bandwidth id={d.id} p={online ? presence : undefined} netHist={netHist} stream={online ? stream : undefined} />}
        {tab === 'web' && <Websites id={d.id} />}
        {tab === 'network' && <NetworkDetails d={d} p={online ? presence : undefined} />}
      </div>
    </>
  )
}

function DeviceActions({ d, online }: { d: Device; online: boolean }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const [menu, setMenu] = useState(false)
  const [msg, setMsg] = useState(false)
  const [move, setMove] = useState(false)
  const [code, setCode] = useState<{ code: string; expiresUtc: string } | null>(null)
  const fail = (e: unknown) => toast.error(e instanceof ApiError ? e.message : 'Request failed - check that the server is running')
  const offlineNote = `${d.hostname} is offline (last seen ${ago(d.lastSeenAt)}). It will run when the device next checks in.`
  const cmd = async (type: string, payload?: string) => {
    try {
      const r = await api.post<{ status: string }>(`/api/devices/${d.id}/commands`, { type, payload })
      if (r.status === 'sent') toast.success('Command delivered over the live channel')
      else toast.info('Command queued', { description: online ? 'Delivered on the next check-in.' : offlineNote })
      qc.invalidateQueries({ queryKey: ['commands', d.id] })
    } catch (e) { fail(e) }
  }
  const state = async (action: string, confirmText?: string) => {
    setMenu(false)
    if (confirmText && !confirm(confirmText)) return
    try {
      await api.post(`/api/devices/${d.id}/${action}`)
      toast.success(`Device ${action === 'retire' ? 'retired' : `${action}d`}`)
      qc.invalidateQueries({ queryKey: ['device', d.id] })
    } catch (e) { fail(e) }
  }
  const uninstallCode = async () => {
    setMenu(false)
    try {
      setCode(await api.post(`/api/devices/${d.id}/uninstall-code`))
      if (!online) toast.warning('Device is offline', { description: 'The code only works after the device checks in and receives it.' })
    } catch (e) { fail(e) }
  }
  return (
    <div className="flex flex-wrap items-center gap-2">
      {d.status === 'pending' && <Button variant="primary" icon={<CheckCircle2 className="size-4" />} onClick={() => state('approve')}>Approve</Button>}
      <Button icon={<RefreshCw className="size-4" />} onClick={() => cmd('collectNow')}>Collect now</Button>
      <Button icon={<Package className="size-4" />} onClick={() => cmd('refreshInventory')}>Refresh inventory</Button>
      <Button icon={<MessageSquare className="size-4" />} onClick={() => setMsg(true)}>Message</Button>
      <div className="relative">
        <Button icon={<MoreHorizontal className="size-4" />} onClick={() => setMenu((m) => !m)} aria-label="More actions" />
        {menu && (
          <div className="absolute right-0 top-11 z-20 w-60 rounded-lg border border-line bg-panel p-1 shadow-md animate-rise" onMouseLeave={() => setMenu(false)}>
            <MenuItem icon={<ListTree className="size-4" />} onClick={() => { setMove(true); setMenu(false) }}>Move to branch…</MenuItem>
            <MenuItem icon={<KeyRound className="size-4" />} onClick={uninstallCode}>Generate one-time uninstall code</MenuItem>
            {d.status === 'active'
              ? <MenuItem icon={<Ban className="size-4" />} onClick={() => state('disable', 'Disable this device? It will stop being able to upload data.')}>Disable device</MenuItem>
              : d.status === 'disabled' && <MenuItem icon={<CheckCircle2 className="size-4" />} onClick={() => state('enable')}>Re-enable device</MenuItem>}
            <MenuItem icon={<XCircle className="size-4" />} onClick={() => state('retire', 'Retire this device? It will be hidden from lists and its certificate rejected.')}>Retire device</MenuItem>
            {can.super && (
              <MenuItem danger icon={<Trash2 className="size-4" />} onClick={() => {
                setMenu(false)
                if (confirm(`Remotely uninstall the agent from ${d.hostname}? This is audited.${online ? '' : `\n\n${offlineNote}`}`)) cmd('uninstall')
              }}>
                Remote uninstall
              </MenuItem>
            )}
          </div>
        )}
      </div>
      <MessageModal open={msg} onClose={() => setMsg(false)} count={1} onSend={async (t, b) => cmd('showMessage', JSON.stringify({ title: t, body: b }))} />
      <MoveModal open={move} onClose={() => setMove(false)} d={d} />
      <Modal open={!!code} onClose={() => setCode(null)} title="One-time uninstall code" footer={<Button variant="primary" onClick={() => setCode(null)}>Done</Button>}>
        <div className="space-y-4 text-sm">
          <p className="text-fg-2">Give this code to the on-site technician. It works only on <b>{d.hostname}</b>, together with local administrator rights, until {dateTime(code?.expiresUtc)}.</p>
          <div className="rounded-lg bg-panel-2 py-5 text-center font-mono text-2xl font-semibold tracking-[0.2em]">{code?.code}</div>
          <Callout tone="warning">The code is pushed to the device as a signed command; the device must be online (or check in) before it can be used.</Callout>
        </div>
      </Modal>
    </div>
  )
}

const MenuItem = ({ icon, children, onClick, danger }: { icon: React.ReactNode; children: React.ReactNode; onClick: () => void; danger?: boolean }) => (
  <button onClick={onClick} className={cx('flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-left text-sm hover:bg-panel-2', danger && 'text-critical')}>{icon}{children}</button>
)

function MoveModal({ open, onClose, d }: { open: boolean; onClose: () => void; d: Device }) {
  const qc = useQueryClient()
  const { data: org } = useQuery({ queryKey: ['org'], queryFn: () => api.get<OrgUnit[]>('/api/org'), enabled: open })
  const [unit, setUnit] = useState(d.orgUnitId ?? '')
  const [tags, setTags] = useState(d.tags ?? '')
  return (
    <Modal open={open} onClose={onClose} title="Move device"
      footer={<><Button onClick={onClose}>Cancel</Button><Button variant="primary" onClick={async () => {
        try {
          await api.put(`/api/devices/${d.id}`, { orgUnitId: unit || null, tags })
          toast.success('Device updated — policy will be re-evaluated'); qc.invalidateQueries({ queryKey: ['device', d.id] }); onClose()
        } catch (e) { toast.error(e instanceof ApiError ? e.message : 'Request failed') }
      }}>Save</Button></>}>
      <div className="space-y-4">
        <Field label="Branch / department">
          <Select value={unit} onChange={(e) => setUnit(e.target.value)} className="w-full">
            <option value="">Unassigned</option>
            {org?.sort((a, b) => a.name.localeCompare(b.name)).map((o) => <option key={o.id} value={o.id}>{o.name} ({o.type})</option>)}
          </Select>
        </Field>
        <Field label="Tags" hint="Comma separated, e.g. laptop, finance"><Input value={tags} onChange={(e) => setTags(e.target.value)} /></Field>
      </div>
    </Modal>
  )
}

// ───────────────────────── Live activity ─────────────────────────
function LiveActivity({ d, p, netHist, stream }: { d: Device; p?: Presence; netHist: NetHist; stream?: StreamState }) {
  const todayIn = Math.max(d.today.bytesIn, p?.todayIn ?? 0)
  const todayOut = Math.max(d.today.bytesOut, p?.todayOut ?? 0)
  return (
    <div className="space-y-4">
      <Card title="Right now" actions={<StreamBadge stream={stream} />}
        subtitle={p ? (stream ? `Streaming live · a new sample every ${stream.intervalMs} ms` : `Updated ${ago(p.lastSeen)} · ${d.liveChannel ? 'live channel connected' : 'heartbeat polling'}`) : `Device offline · last seen ${ago(d.lastSeenAt)}`}>
        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          <MiniStat icon={<AppWindow className="size-3.5" />} label="Foreground app" value={p?.currentApp ? appName(p.currentApp) : '—'}
            sub={p ? (p.locked ? 'Screen locked' : p.idle ? 'Idle' : `In use by ${p.user ?? d.loggedInUser ?? 'unknown'}`) : 'Offline'} />
          <MiniStat icon={<ArrowDown className="size-3.5 text-s1" />} label="Download now" value={bps(p?.netInBps)} spark={netHist.map((h) => h.in)} color={S.down} />
          <MiniStat icon={<ArrowUp className="size-3.5 text-s2" />} label="Upload now" value={bps(p?.netOutBps)} spark={netHist.map((h) => h.out)} color={S.up} />
          <MiniStat icon={<Cpu className="size-3.5" />} label="CPU" value={p ? `${p.cpu.toFixed(1)}%` : '—'} spark={netHist.map((h) => h.cpu ?? 0)} sparkMax={100} color="var(--brand)" />
          <MiniStat icon={<MemoryStick className="size-3.5" />} label="Memory" value={p ? `${p.ram.toFixed(0)}%` : '—'} spark={netHist.map((h) => h.ram ?? 0)} sparkMax={100} color="var(--muted)" />
          <MiniStat icon={<Wifi className="size-3.5" />} label="Data today" value={bytes(todayIn + todayOut)} sub={`↓ ${bytes(todayIn)} · ↑ ${bytes(todayOut)}`} />
        </div>
      </Card>
      <Apps id={d.id} p={p} />
    </div>
  )
}

// ───────────────────────── Network ─────────────────────────
function NetworkDetails({ d, p }: { d: Device; p?: Presence }) {
  const nics = d.hardware?.nics ?? []
  return (
    <div className="grid gap-4 lg:grid-cols-3">
      <Card title="Connection">
        <Kv items={[
          ['Status', p ? <Badge tone="good">Online</Badge> : <Badge>Offline · {ago(d.lastSeenAt)}</Badge>],
          ['Connection type', p?.connectionType ?? '—'],
          ['Local IP', <span className="font-mono text-xs">{p?.ip ?? d.lastIp ?? '—'}</span>],
          ['Public IP', <span className="font-mono text-xs">{d.publicIp ?? '—'}</span>],
          ['Domain', d.domain ?? d.hardware?.domain ?? '—'],
          ['Server link', d.liveChannel ? <Badge tone="good">Live channel</Badge> : <Badge>Heartbeat polling</Badge>],
        ]} />
      </Card>
      <Card className="lg:col-span-2" title="Network adapters" subtitle={d.hardwareCollectedAt ? `Collected ${ago(d.hardwareCollectedAt)}` : undefined} pad={false}>
        {nics.length === 0 ? <Empty icon={<Network className="size-5" />} title="Adapter details not collected yet" /> : (
          <Table>
            <thead><tr><th>Adapter</th><th>Type</th><th>IP</th><th>MAC</th><th>Speed</th></tr></thead>
            <tbody>
              {nics.map((n) => (
                <tr key={n.name + n.mac}><td>{n.name}</td><td><Badge>{n.type}</Badge></td><td className="font-mono text-xs">{n.ip ?? '—'}</td><td className="font-mono text-xs">{n.mac ?? '—'}</td><td className="tabular">{n.speedMbps ? `${n.speedMbps} Mbps` : '—'}</td></tr>
              ))}
            </tbody>
          </Table>
        )}
      </Card>
    </div>
  )
}

function MiniStat({ icon, label, value, sub, spark, sparkMax, color }: { icon: React.ReactNode; label: string; value: string; sub?: string; spark?: number[]; sparkMax?: number; color?: string }) {
  return (
    <div className="rounded-lg border border-line bg-panel-2/40 p-3">
      <div className="flex items-center gap-1.5 text-xs text-muted">{icon}{label}</div>
      <div className="mt-1 text-lg font-semibold tabular">{value}</div>
      {spark ? <Sparkline values={spark} max={sparkMax} color={color} height={22} /> : <div className="text-[11px] text-muted">{sub}</div>}
    </div>
  )
}

// ───────────────────────── Applications ─────────────────────────
interface AppsData {
  apps: { app: string; activeSec: number; titles?: { title: string; sec: number }[] }[]
  timeline: { hour: number; activeSec: number; idleSec: number }[]
  totalActiveSec: number; totalIdleSec: number; users: string[]
  processes?: { pid: number; name: string; user?: string; memoryMb: number; path?: string }[]; processesCollectedAt?: string
}
function Apps({ id, p }: { id: string; p?: Presence }) {
  const [days, setDays] = useState(1)
  const [open, setOpen] = useState<string | null>(null)
  const { data } = useQuery({ queryKey: ['apps', id, days], queryFn: () => api.get<AppsData>(`/api/devices/${id}/apps?days=${days}`) })
  const [pq, setPq] = useState('')
  if (!data) return <Skeleton className="h-96" />
  const prod = data.totalActiveSec + data.totalIdleSec ? Math.round((data.totalActiveSec / (data.totalActiveSec + data.totalIdleSec)) * 100) : 0
  return (
    <div className="grid gap-4 xl:grid-cols-3">
      <Card className="xl:col-span-3" title="Activity by hour" subtitle={days === 1 ? 'Today, minutes per hour' : `Average per day over ${days} days`}
        actions={<Segmented value={days} onChange={setDays} options={[{ value: 1, label: 'Today' }, { value: 7, label: '7 days' }, { value: 30, label: '30 days' }]} />}>
        <div className="mb-3 flex flex-wrap items-center gap-6 text-sm">
          <div><span className="text-muted">Active </span><span className="font-semibold tabular">{duration(data.totalActiveSec)}</span></div>
          <div><span className="text-muted">Idle </span><span className="font-semibold tabular">{duration(data.totalIdleSec)}</span></div>
          <div><span className="text-muted">Active share </span><span className="font-semibold tabular">{prod}%</span></div>
          {p?.currentApp && <div className="ml-auto inline-flex items-center gap-2 text-xs"><OnlineDot online />Now: <b>{appName(p.currentApp)}</b></div>}
          <Legend items={[{ label: 'Active', color: S.active }, { label: 'Idle', color: S.idle }]} />
        </div>
        <ActivityBars data={data.timeline} />
      </Card>
      <Card className="xl:col-span-2" title="Applications by active time" subtitle="Click an application to see top window titles (masked per policy)">
        {data.apps.length === 0 ? <Empty icon={<AppWindow className="size-5" />} title="No application usage in this period" /> : (
          <ul className="divide-y divide-line">
            {data.apps.slice(0, 20).map((a) => {
              const pctv = (a.activeSec / Math.max(1, data.apps[0].activeSec)) * 100
              return (
                <li key={a.app} className="py-2">
                  <button className="w-full text-left" onClick={() => setOpen(open === a.app ? null : a.app)}>
                    <div className="mb-1 flex items-center justify-between gap-3 text-sm">
                      <span className="flex items-center gap-2 font-medium">{appName(a.app)}<span className="font-mono text-[11px] font-normal text-muted">{a.app}</span></span>
                      <span className="tabular text-fg-2">{duration(a.activeSec)}</span>
                    </div>
                    <div className="h-1.5 rounded-full bg-panel-2"><div className="h-full rounded-full bg-s1" style={{ width: `${pctv}%` }} /></div>
                  </button>
                  {open === a.app && a.titles && (
                    <ul className="mt-2 space-y-1 rounded-md bg-panel-2 p-2.5 text-xs">
                      {a.titles.length === 0 && <li className="text-muted">No window titles captured (privacy policy)</li>}
                      {a.titles.map((t) => <li key={t.title} className="flex justify-between gap-3"><span className="truncate">{t.title}</span><span className="shrink-0 tabular text-muted">{duration(t.sec)}</span></li>)}
                    </ul>
                  )}
                </li>
              )
            })}
          </ul>
        )}
      </Card>
      <Card title="Running processes" subtitle={data.processesCollectedAt ? `Snapshot ${ago(data.processesCollectedAt)}` : 'No snapshot yet'} pad={false}>
        <div className="p-3 pb-0"><Input value={pq} onChange={(e) => setPq(e.target.value)} placeholder="Filter processes…" /></div>
        <div className="max-h-130 overflow-y-auto">
          <Table>
            <thead><tr><th>Process</th><th>User</th><th className="text-right">Memory</th></tr></thead>
            <tbody>
              {(data.processes ?? []).filter((x) => !pq || x.name.toLowerCase().includes(pq.toLowerCase())).slice(0, 150).map((x) => (
                <tr key={x.pid} title={x.path}><td className="max-w-40 truncate">{x.name}</td><td className="max-w-28 truncate text-xs text-muted">{x.user?.split('\\').pop() ?? '—'}</td><td className="text-right tabular text-xs">{num(x.memoryMb)} MB</td></tr>
              ))}
            </tbody>
          </Table>
        </div>
      </Card>
    </div>
  )
}

// ───────────────────────── Bandwidth ─────────────────────────
interface BandwidthData {
  daily: { day: string; bytesIn: number; bytesOut: number }[]
  hourly: { hour: number; bytesIn: number; bytesOut: number }[]
  adapters: { adapter: string; type: string; bytesIn: number; bytesOut: number }[]
  processes: { process: string; bytesIn: number; bytesOut: number }[]
  rangeDays: number
}
function Bandwidth({ id, p, netHist, stream }: { id: string; p?: Presence; netHist: NetHist; stream?: StreamState }) {
  const [days, setDays] = useState(30)
  const { data } = useQuery({ queryKey: ['bw', id, days], queryFn: () => api.get<BandwidthData>(`/api/devices/${id}/bandwidth?days=${days}`) })
  const legend = <Legend items={[{ label: 'Download', color: S.down }, { label: 'Upload', color: S.up }]} />
  const total = data?.daily.reduce((s, x) => s + x.bytesIn + x.bytesOut, 0) ?? 0
  return (
    <div className="grid gap-4 xl:grid-cols-3">
      <Card className="xl:col-span-2" title="Live throughput"
        subtitle={p ? (stream ? `Streaming live · every ${stream.intervalMs} ms` : 'From heartbeats (every few seconds) until the live stream connects') : 'Device offline'}
        actions={<div className="flex items-center gap-3"><StreamBadge stream={stream} />{legend}</div>}>
        {netHist.length > 1 ? (
          <Lines data={netHist.map((h) => ({ ...h }))} xKey="t" yFormat={(v) => bps(v)} height={200} animate={!stream}
            series={[{ key: 'in', name: 'Download', color: S.down }, { key: 'out', name: 'Upload', color: S.up }]} />
        ) : <Empty icon={<Activity className="size-5" />} title={p ? 'Collecting live samples…' : 'No live data'}>{p ? `Current: ↓ ${bps(p.netInBps)} · ↑ ${bps(p.netOutBps)}` : 'Live throughput appears when the device is online.'}</Empty>}
      </Card>
      <Card title="Today by hour" actions={legend}>
        {data ? <UsageBars data={data.hourly.map((h) => ({ ...h, label: `${String(h.hour).padStart(2, '0')}:00` }))} xKey="label" height={200} /> : <Skeleton className="h-48" />}
      </Card>
      <Card className="xl:col-span-3" title="Daily data usage" subtitle={`${bytes(total)} over ${days} days`}
        actions={<div className="flex items-center gap-3">{legend}<Segmented value={days} onChange={setDays} options={[{ value: 7, label: '7d' }, { value: 30, label: '30d' }, { value: 90, label: '90d' }]} /></div>}>
        {data ? <UsageBars data={data.daily} xKey="day" xFormat={shortDay} height={240} /> : <Skeleton className="h-60" />}
      </Card>
      <Card className="xl:col-span-2" title="Per-application traffic" subtitle={`Last ${data?.rangeDays ?? 7} days · from kernel ETW network events`} pad={false}>
        {data?.processes.length ? (
          <Table>
            <thead><tr><th>Process</th><th className="text-right">Download</th><th className="text-right">Upload</th><th className="w-1/3">Share</th></tr></thead>
            <tbody>
              {data.processes.map((x) => {
                const tot = data.processes.reduce((s, y) => s + y.bytesIn + y.bytesOut, 0) || 1
                return (
                  <tr key={x.process}>
                    <td className="font-medium">{appName(x.process)} <span className="font-mono text-[11px] font-normal text-muted">{x.process}</span></td>
                    <td className="text-right tabular">{bytes(x.bytesIn)}</td>
                    <td className="text-right tabular">{bytes(x.bytesOut)}</td>
                    <td><div className="h-1.5 rounded-full bg-panel-2"><div className="h-full rounded-full bg-s1" style={{ width: `${((x.bytesIn + x.bytesOut) / tot) * 100}%` }} /></div></td>
                  </tr>
                )
              })}
            </tbody>
          </Table>
        ) : <Empty icon={<Network className="size-5" />} title="No per-process data">Requires the agent to run as a service (kernel ETW).</Empty>}
      </Card>
      <Card title="By adapter" subtitle={`Last ${data?.rangeDays ?? 7} days`}>
        {data?.adapters.length ? (
          <RankBars rows={data.adapters.map((a) => ({ key: a.adapter, label: <span className="inline-flex items-center gap-2">{a.adapter}<Badge>{a.type}</Badge></span>, value: a.bytesIn + a.bytesOut }))} format={(v) => bytes(v)} />
        ) : <Empty title="No adapter data" />}
      </Card>
    </div>
  )
}

// ───────────────────────── Websites ─────────────────────────
interface WebData {
  domains: { domain: string; category: string; hits: number; lastVisit: string; bytesIn: number; bytesOut: number; hosts: string[]; users: string[]; processes: string[] }[]
  categories: { category: string; hits: number; bytes: number }[]
  totalBytesIn: number; totalBytesOut: number
}
interface LiveWeb { at?: string; online: boolean; sites: { domain: string; category: string; inBps: number; outBps: number }[] }

function LiveSites({ id }: { id: string }) {
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ['web-live', id], queryFn: () => api.get<LiveWeb>(`/api/devices/${id}/web/live`), refetchInterval: 5_000 })
  // a fresh heartbeat carries new live numbers - refetch right away instead of waiting for the next poll
  useHubEvent<Presence>('presence', (p) => { if (p.deviceId === id) qc.invalidateQueries({ queryKey: ['web-live', id] }) })
  const sites = data?.sites.filter((s) => s.inBps + s.outBps > 0) ?? []
  const max = Math.max(1, ...sites.map((s) => s.inBps + s.outBps))
  return (
    <Card className="xl:col-span-3" title="Using the internet now"
      subtitle={!data ? 'Loading…' : !data.online ? 'Device offline' : data.at ? `Live · updated ${ago(data.at)} · traffic per site since the previous heartbeat` : 'Waiting for the first live sample…'}
      actions={<Legend items={[{ label: 'Download', color: S.down }, { label: 'Upload', color: S.up }]} />}>
      {sites.length === 0 ? (
        <Empty icon={<Activity className="size-5" />} title={data?.online ? 'No website traffic right now' : 'No live data'}>
          {data?.online ? 'Sites appear here as soon as the device exchanges data with them.' : 'Live usage appears when the device is online.'}
        </Empty>
      ) : (
        <ul className="space-y-2.5">
          {sites.map((s) => (
            <li key={s.domain}>
              <div className="mb-1 flex items-center justify-between gap-3 text-sm">
                <span className="flex items-center gap-2 font-medium"><OnlineDot online />{s.domain}<Badge>{s.category}</Badge></span>
                <span className="tabular text-xs text-fg-2">↓ {bps(s.inBps)} · ↑ {bps(s.outBps)}</span>
              </div>
              <div className="flex h-1.5 overflow-hidden rounded-full bg-panel-2">
                <div className="h-full" style={{ width: `${(s.inBps / max) * 100}%`, background: S.down }} />
                <div className="h-full" style={{ width: `${(s.outBps / max) * 100}%`, background: S.up }} />
              </div>
            </li>
          ))}
        </ul>
      )}
    </Card>
  )
}

function Websites({ id }: { id: string }) {
  const [days, setDays] = useState(7)
  const [cat, setCat] = useState('')
  const [q, setQ] = useState('')
  const [sort, setSort] = useState<'data' | 'hits'>('data')
  const [showSystem, setShowSystem] = useState(false)
  const { data } = useQuery({ queryKey: ['web', id, days], queryFn: () => api.get<WebData>(`/api/devices/${id}/web?days=${days}`), refetchInterval: 60_000 })
  if (!data) return <Skeleton className="h-96" />
  const rows = data.domains.filter((d) => (showSystem || d.category !== 'System') && (!cat || d.category === cat) && (!q || d.domain.includes(q.toLowerCase())))
    .sort((a, b) => sort === 'data' ? (b.bytesIn + b.bytesOut) - (a.bytesIn + a.bytesOut) || b.hits - a.hits : b.hits - a.hits)
  const total = data.totalBytesIn + data.totalBytesOut
  return (
    <div className="grid gap-4 xl:grid-cols-3">
      <LiveSites id={id} />
      <Card className="xl:col-span-3">
        <div className="grid gap-3 sm:grid-cols-3">
          <MiniStat icon={<Globe className="size-3.5" />} label={`Website data · last ${days === 1 ? 'day' : `${days} days`}`} value={bytes(total)} sub={`${num(data.domains.length)} sites`} />
          <MiniStat icon={<ArrowDown className="size-3.5 text-s1" />} label="Downloaded from websites" value={bytes(data.totalBytesIn)} />
          <MiniStat icon={<ArrowUp className="size-3.5 text-s2" />} label="Uploaded to websites" value={bytes(data.totalBytesOut)} />
        </div>
      </Card>
      <Card title="Categories" subtitle={`Last ${days} days`} actions={<Segmented value={days} onChange={setDays} options={[{ value: 1, label: '1d' }, { value: 7, label: '7d' }, { value: 30, label: '30d' }]} />}>
        <RankBars color="var(--series-7)" rows={data.categories.filter((c) => showSystem || c.category !== 'System').map((c) => ({ key: c.category, label: <span className={cx(cat === c.category && 'font-semibold underline underline-offset-4')}>{c.category}</span>, value: c.hits }))}
          format={(v) => num(v)} onClick={(k) => setCat(cat === k ? '' : k)} />
        <label className="mt-4 flex items-center gap-2 text-xs text-muted"><input type="checkbox" checked={showSystem} onChange={(e) => setShowSystem(e.target.checked)} />Show background/system domains</label>
      </Card>
      <Card className="xl:col-span-2" pad={false} title="Sites visited" subtitle="Per site (DNS) with the data exchanged with it. Full page URLs require the optional browser extension and HR/legal approval.">
        <div className="flex flex-wrap items-center gap-2 px-6 pb-3 pt-4">
          <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Filter sites…" className="max-w-xs" />
          <Segmented value={sort} onChange={setSort} options={[{ value: 'data', label: 'Most data' }, { value: 'hits', label: 'Most lookups' }]} />
          {cat && <Badge tone="brand">{cat} <button onClick={() => setCat('')} className="ml-1">×</button></Badge>}
        </div>
        {rows.length === 0 ? <Empty icon={<Globe className="size-5" />} title="No sites in this view" /> : (
          <Table>
            <thead><tr><th>Site</th><th>Category</th><th className="text-right">Download</th><th className="text-right">Upload</th><th className="text-right">Total</th><th className="text-right">Lookups</th><th className="hidden md:table-cell">Users</th><th>Last</th></tr></thead>
            <tbody>
              {rows.slice(0, 200).map((d) => (
                <tr key={d.domain}>
                  <td><div className="font-medium">{d.domain}</div><div className="max-w-64 truncate text-[11px] text-muted">{d.hosts.join(', ')}</div></td>
                  <td><Badge tone={['Social', 'Streaming', 'Gaming', 'Shopping'].includes(d.category) ? 'warning' : 'neutral'}>{d.category}</Badge></td>
                  <td className="text-right tabular text-xs">{d.bytesIn ? bytes(d.bytesIn) : '—'}</td>
                  <td className="text-right tabular text-xs">{d.bytesOut ? bytes(d.bytesOut) : '—'}</td>
                  <td className="text-right font-medium tabular">{d.bytesIn + d.bytesOut ? bytes(d.bytesIn + d.bytesOut) : '—'}</td>
                  <td className="text-right tabular">{num(d.hits)}</td>
                  <td className="hidden max-w-36 truncate text-xs text-fg-2 md:table-cell">{d.users.map((u) => u.split('\\').pop()).join(', ') || '—'}</td>
                  <td className="text-xs text-muted">{ago(d.lastVisit)}</td>
                </tr>
              ))}
            </tbody>
          </Table>
        )}
      </Card>
    </div>
  )
}

// ───────────────────────── Live stream ─────────────────────────
type NetHist = { t: string; in: number; out: number; cpu?: number; ram?: number }[]
interface StreamState { intervalMs: number; lastAt: number }
/** ~60 s of history at the 500 ms stream rate. */
const HIST = 120

function StreamBadge({ stream }: { stream?: StreamState }) {
  if (!stream) return null
  return (
    <span className="inline-flex items-center gap-1.5 rounded-full bg-good/12 px-2 py-0.5 text-[11px] font-medium text-good-ink">
      <span className="pulse-dot size-1.5 rounded-full bg-good" />Live · {stream.intervalMs} ms
    </span>
  )
}
