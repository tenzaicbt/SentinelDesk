import { useMemo, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router-dom'
import { toast } from 'sonner'
import { ArrowDown, ArrowUp, Bell, Download, Laptop, MessageSquare, Monitor, RefreshCw, Search, Trash2 } from 'lucide-react'
import { api, ApiError } from '../lib/api'
import { confirmDialog } from '../components/dialog'
import { useAuth } from '../lib/auth'
import { useHubEvent, useHubGroup, type Presence } from '../lib/hub'
import { ago, appName, bytes } from '../lib/format'
import { Badge, Button, Card, cx, Empty, Field, Input, Meter, Modal, OnlineDot, PageHeader, Select, Skeleton, Table } from '../components/ui'

export interface DeviceRow {
  id: string; hostname: string; domain?: string; user?: string; ip?: string; publicIp?: string; os: string; osBuild: string
  agentVersion: string; status: string; online: boolean; lastSeen?: string; orgUnitId?: string; branch?: string
  manufacturer?: string; model?: string; serial?: string; tags?: string; isSimulated: boolean; todayBytes: number
  cpu?: number; ram?: number; currentApp?: string; alerts: number
}
export interface OrgUnit { id: string; parentId?: string; name: string; type: string; devices: number; online: number; lat?: number; lng?: number; policy?: { policyId: string; name: string } }

type SortKey = 'hostname' | 'user' | 'branch' | 'todayBytes' | 'cpu' | 'lastSeen'

export default function Devices() {
  const [params, setParams] = useSearchParams()
  const { can } = useAuth()
  const qc = useQueryClient()
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState('')
  const [online, setOnline] = useState('')
  const [sort, setSort] = useState<{ key: SortKey; dir: 1 | -1 }>({ key: 'hostname', dir: 1 })
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [msgOpen, setMsgOpen] = useState(false)
  const orgUnitId = params.get('orgUnitId') ?? ''

  const { data: org } = useQuery({ queryKey: ['org'], queryFn: () => api.get<OrgUnit[]>('/api/org') })
  const { data, isLoading, refetch } = useQuery({
    queryKey: ['devices', orgUnitId, status],
    queryFn: () => api.get<DeviceRow[]>(`/api/devices?${new URLSearchParams({ ...(orgUnitId && { orgUnitId }), ...(status && { status }) })}`),
    refetchInterval: 30_000,
  })

  // live patch rows from heartbeats
  const [live, setLive] = useState<Record<string, Presence>>({})
  useHubGroup('WatchLive', 'UnwatchLive')
  useHubEvent<Presence>('presence', (p) => setLive((s) => ({ ...s, [p.deviceId]: p })))

  const rows = useMemo(() => {
    let r = (data ?? []).map((d) => {
      const p = live[d.id]
      return p ? { ...d, online: true, cpu: p.cpu, ram: p.ram, currentApp: p.currentApp, user: p.user ?? d.user, lastSeen: p.lastSeen, todayBytes: Math.max(d.todayBytes, p.todayIn + p.todayOut) } : d
    })
    const q = search.trim().toLowerCase()
    if (q) r = r.filter((d) => [d.hostname, d.user, d.ip, d.serial, d.model, d.branch].some((v) => v?.toLowerCase().includes(q)))
    if (online) r = r.filter((d) => d.online === (online === 'online'))
    return [...r].sort((a, b) => {
      const av = a[sort.key] ?? '', bv = b[sort.key] ?? ''
      return (av < bv ? -1 : av > bv ? 1 : 0) * sort.dir
    })
  }, [data, live, search, online, sort])

  const toggleSort = (key: SortKey) => setSort((s) => ({ key, dir: s.key === key ? (s.dir === 1 ? -1 : 1) : key === 'todayBytes' || key === 'cpu' ? -1 : 1 }))
  const Th = ({ k, children, className }: { k: SortKey; children: React.ReactNode; className?: string }) => (
    <th className={className}>
      <button onClick={() => toggleSort(k)} className="inline-flex items-center gap-1 uppercase hover:text-fg">
        {children}{sort.key === k && (sort.dir === 1 ? <ArrowUp className="size-3" /> : <ArrowDown className="size-3" />)}
      </button>
    </th>
  )

  const bulk = async (type: string, payload?: string) => {
    const r = await api.post<{ queued: number }>('/api/devices/bulk-command', { deviceIds: [...selected], type, payload })
    toast.success(`Command queued for ${r.queued} devices`)
    setSelected(new Set())
  }

  const remove = async () => {
    const n = selected.size
    if (!(await confirmDialog({
      title: `Delete ${n} device${n === 1 ? '' : 's'}?`,
      message: 'The selected devices and all of their history are permanently deleted. This cannot be undone. Agents still installed on these PCs stop reporting but are not removed - use Remote uninstall for that.',
      confirmLabel: n === 1 ? 'Delete device' : `Delete ${n} devices`, tone: 'danger',
    }))) return
    try {
      const r = await api.post<{ deleted: number }>('/api/devices/delete', { deviceIds: [...selected] })
      toast.success(`${r.deleted} device${r.deleted === 1 ? '' : 's'} deleted`)
      setSelected(new Set())
      qc.invalidateQueries({ queryKey: ['devices'] })
    } catch (e) { toast.error(e instanceof ApiError ? e.message : 'Delete failed') }
  }

  const exportCsv = () => {
    const head = ['Hostname', 'User', 'Branch', 'IP', 'OS', 'Model', 'Serial', 'Agent', 'Online', 'Last seen', 'Today MB']
    const lines = rows.map((d) => [d.hostname, d.user, d.branch, d.ip, `${d.os} ${d.osBuild}`, d.model, d.serial, d.agentVersion, d.online, d.lastSeen, (d.todayBytes / 1048576).toFixed(1)]
      .map((v) => `"${String(v ?? '').replace(/"/g, '""')}"`).join(','))
    const a = document.createElement('a')
    a.href = URL.createObjectURL(new Blob(['﻿' + [head.join(','), ...lines].join('\n')], { type: 'text/csv' }))
    a.download = 'devices.csv'
    a.click()
  }

  const onlineCount = rows.filter((r) => r.online).length

  return (
    <>
      <PageHeader title="Devices" icon={<Monitor className="size-5" />}
        subtitle={data ? `${rows.length} devices · ${onlineCount} online` : 'Loading…'}
        actions={<>
          <Button icon={<Download className="size-4" />} onClick={exportCsv}>Export</Button>
          <Button icon={<RefreshCw className="size-4" />} onClick={() => refetch()}>Refresh</Button>
        </>} />

      <Card pad={false}>
        <div className="flex flex-wrap items-center gap-2 border-b border-line p-3">
          <div className="relative min-w-56 flex-1">
            <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted" />
            <Input value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Filter by hostname, user, IP, serial, model…" className="pl-9" />
          </div>
          <Select value={orgUnitId} onChange={(e) => { const v = e.target.value; setParams(v ? { orgUnitId: v } : {}) }}>
            <option value="">All branches</option>
            {org?.filter((o) => o.type !== 'organization').sort((a, b) => a.name.localeCompare(b.name)).map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
          </Select>
          <Select value={online} onChange={(e) => setOnline(e.target.value)}>
            <option value="">Any connectivity</option>
            <option value="online">Online</option>
            <option value="offline">Offline</option>
          </Select>
          <Select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Active & pending</option>
            <option value="active">Active</option>
            <option value="pending">Pending approval</option>
            <option value="disabled">Disabled</option>
            <option value="retired">Retired</option>
          </Select>
        </div>

        {selected.size > 0 && can.write && (
          <div className="flex flex-wrap items-center gap-2 border-b border-line bg-brand-soft px-4 py-2 text-sm">
            <span className="font-medium">{selected.size} selected</span>
            <Button size="sm" icon={<RefreshCw className="size-3.5" />} onClick={() => bulk('refreshInventory')}>Refresh inventory</Button>
            <Button size="sm" icon={<Download className="size-3.5" />} onClick={() => bulk('collectNow')}>Collect now</Button>
            <Button size="sm" icon={<MessageSquare className="size-3.5" />} onClick={() => setMsgOpen(true)}>Send message</Button>
            {can.super && <Button size="sm" variant="danger" icon={<Trash2 className="size-3.5" />} onClick={remove}>Delete</Button>}
            <button className="ml-auto text-xs text-muted hover:text-fg" onClick={() => setSelected(new Set())}>Clear</button>
          </div>
        )}

        {isLoading ? (
          <div className="space-y-2 p-4">{Array.from({ length: 8 }).map((_, i) => <Skeleton key={i} className="h-10" />)}</div>
        ) : rows.length === 0 ? (
          <Empty icon={<Monitor className="size-5" />} title="No devices match">Adjust the filters, or enroll agents from Agent management.</Empty>
        ) : (
          <Table>
            <thead>
              <tr>
                {can.write && <th className="w-8"><input type="checkbox" aria-label="Select all" checked={selected.size === rows.length} onChange={(e) => setSelected(e.target.checked ? new Set(rows.map((r) => r.id)) : new Set())} /></th>}
                <Th k="hostname">Device</Th>
                <Th k="user">User</Th>
                <Th k="branch" className="hidden md:table-cell">Branch</Th>
                <th className="hidden xl:table-cell">Now</th>
                <Th k="cpu" className="hidden lg:table-cell">CPU / RAM</Th>
                <Th k="todayBytes">Data today</Th>
                <Th k="lastSeen" className="hidden sm:table-cell">Last seen</Th>
              </tr>
            </thead>
            <tbody>
              {rows.map((d) => (
                <tr key={d.id} className={cx('transition-colors hover:bg-panel-2/60', selected.has(d.id) && 'bg-brand-soft/60')}>
                  {can.write && <td><input type="checkbox" aria-label={`Select ${d.hostname}`} checked={selected.has(d.id)} onChange={(e) => {
                    const s = new Set(selected); if (e.target.checked) s.add(d.id); else s.delete(d.id); setSelected(s)
                  }} /></td>}
                  <td>
                    <Link to={`/devices/${d.id}`} className="group flex items-center gap-3">
                      <div className="relative grid size-9 shrink-0 place-items-center rounded-lg bg-panel-2 text-fg-2">
                        {d.tags?.includes('laptop') || /book|thinkpad|latitude|notebook/i.test(d.model ?? '') ? <Laptop className="size-4" /> : <Monitor className="size-4" />}
                        <OnlineDot online={d.online} className="absolute -right-0.5 -top-0.5 ring-2 ring-panel" />
                      </div>
                      <div className="min-w-0">
                        <div className="flex items-center gap-1.5 font-medium group-hover:text-brand-ink">
                          {d.hostname}
                          {d.status !== 'active' && <Badge tone={d.status === 'pending' ? 'warning' : 'neutral'}>{d.status}</Badge>}
                          {d.alerts > 0 && <Badge tone="critical" icon={<Bell className="size-3" />}>{d.alerts}</Badge>}
                        </div>
                        <div className="truncate text-xs text-muted">{[d.model, d.ip].filter(Boolean).join(' · ')}</div>
                      </div>
                    </Link>
                  </td>
                  <td className="max-w-44 truncate text-fg-2">{d.user?.split('\\').pop() ?? '—'}</td>
                  <td className="hidden text-fg-2 md:table-cell">{d.branch ?? <span className="text-muted">Unassigned</span>}</td>
                  <td className="hidden max-w-40 truncate text-fg-2 xl:table-cell">{d.online ? (d.currentApp ? appName(d.currentApp) : <span className="text-muted">Idle</span>) : '—'}</td>
                  <td className="hidden w-40 lg:table-cell">
                    {d.online && d.cpu !== undefined && d.cpu !== null ? (
                      <div className="space-y-1.5"><Meter value={d.cpu} /><Meter value={d.ram} /></div>
                    ) : <span className="text-muted">—</span>}
                  </td>
                  <td className="tabular">{bytes(d.todayBytes)}</td>
                  <td className="hidden text-xs text-muted sm:table-cell">{d.online ? <span className="text-good-ink">Now</span> : ago(d.lastSeen)}</td>
                </tr>
              ))}
            </tbody>
          </Table>
        )}
      </Card>
      <MessageModal open={msgOpen} onClose={() => setMsgOpen(false)} onSend={(t, b) => bulk('showMessage', JSON.stringify({ title: t, body: b }))} count={selected.size} />
    </>
  )
}

export function MessageModal({ open, onClose, onSend, count }: { open: boolean; onClose: () => void; onSend: (title: string, body: string) => Promise<void>; count: number }) {
  const [title, setTitle] = useState('SentinelDesk')
  const [body, setBody] = useState('')
  const [busy, setBusy] = useState(false)
  return (
    <Modal open={open} onClose={onClose} title={`Send tray notification${count > 1 ? ` to ${count} devices` : ''}`}
      footer={<>
        <Button onClick={onClose}>Cancel</Button>
        <Button variant="primary" loading={busy} disabled={!body.trim()} onClick={async () => { setBusy(true); try { await onSend(title, body); onClose(); setBody('') } finally { setBusy(false) } }}>Send</Button>
      </>}>
      <div className="space-y-3">
        <Field label="Title"><Input value={title} onChange={(e) => setTitle(e.target.value)} maxLength={60} /></Field>
        <Field label="Message" hint="Shown as a Windows notification from the SentinelDesk tray icon">
          <textarea value={body} onChange={(e) => setBody(e.target.value)} rows={4} maxLength={250}
            className="w-full rounded-lg border border-line-strong bg-panel px-3 py-2 text-sm focus:border-brand focus:outline-none focus:ring-3 focus:ring-brand-soft" />
        </Field>
      </div>
    </Modal>
  )
}
