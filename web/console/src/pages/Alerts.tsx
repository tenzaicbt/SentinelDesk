import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { Bell, Check, CheckCheck, Pencil, Plus, Settings2, Trash2 } from 'lucide-react'
import { api, ApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { useHubEvent } from '../lib/hub'
import { ago, dateTime } from '../lib/format'
import { Badge, Button, Card, Empty, Field, Input, Modal, PageHeader, Segmented, Select, SeverityBadge, Skeleton, Switch, Table, Tabs } from '../components/ui'
import { confirmDialog } from '../components/dialog'

interface AlertRow { id: number; title: string; details?: string; severity: string; status: string; triggeredAt: string; handledBy?: string; handledAt?: string; deviceId: string; hostname: string; rule?: string }
interface Rule { id: string; name: string; type: string; threshold: number; parameter?: string; severity: string; enabled: boolean; notifyEmail: boolean; notifyWebhook: boolean }

const ruleTypes: Record<string, { label: string; threshold?: string; parameter?: string }> = {
  dataUsageDaily: { label: 'Daily data usage above', threshold: 'MB per device per day' },
  agentOffline: { label: 'Agent offline for', threshold: 'minutes' },
  cpuHigh: { label: 'Sustained CPU above', threshold: '% (10-minute average)' },
  diskLow: { label: 'System disk usage above', threshold: '%' },
  software: { label: 'Unapproved software installed', parameter: 'Comma-separated names (substring match)' },
  tamper: { label: 'Tamper attempt (stop/kill/uninstall/binary change)' },
  category: { label: 'Website category visited', parameter: 'Comma-separated categories, e.g. Gaming, Streaming' },
  usb: { label: 'USB storage connected' },
}

export default function Alerts() {
  const [tab, setTab] = useState<'alerts' | 'rules'>('alerts')
  return (
    <>
      <PageHeader title="Alerts" icon={<Bell className="size-5" />} subtitle="Rule-based detection across the fleet · notifications by console, email and Teams/webhook" />
      <Tabs value={tab} onChange={setTab} tabs={[{ id: 'alerts', label: 'Alerts', icon: <Bell className="size-3.5" /> }, { id: 'rules', label: 'Rules', icon: <Settings2 className="size-3.5" /> }]} />
      <div className="mt-3">{tab === 'alerts' ? <AlertList /> : <Rules />}</div>
    </>
  )
}

function AlertList() {
  const { can } = useAuth()
  const qc = useQueryClient()
  const [status, setStatus] = useState('open')
  const [severity, setSeverity] = useState('')
  const [sel, setSel] = useState<Set<number>>(new Set())
  const { data, refetch } = useQuery({ queryKey: ['alerts', status, severity], queryFn: () => api.get<AlertRow[]>(`/api/alerts?status=${status}${severity ? `&severity=${severity}` : ''}`) })
  useHubEvent('alert', () => refetch())

  const act = async (action: 'acknowledge' | 'resolve', ids: number[]) => {
    await api.post(`/api/alerts/${action}`, { ids })
    toast.success(`${ids.length} alert(s) ${action === 'resolve' ? 'resolved' : 'acknowledged'}`)
    setSel(new Set())
    qc.invalidateQueries({ queryKey: ['alerts'] })
    qc.invalidateQueries({ queryKey: ['alert-count'] })
  }

  return (
    <Card pad={false}>
      <div className="flex flex-wrap items-center gap-2 border-b border-line p-3">
        <Segmented value={status} onChange={setStatus} options={[{ value: 'open', label: 'Open' }, { value: 'acknowledged', label: 'Acknowledged' }, { value: 'resolved', label: 'Resolved' }, { value: 'all', label: 'All' }]} />
        <Select value={severity} onChange={(e) => setSeverity(e.target.value)}>
          <option value="">All severities</option><option value="critical">Critical</option><option value="warning">Warning</option><option value="info">Info</option>
        </Select>
        {can.write && sel.size > 0 && (
          <div className="ml-auto flex gap-2">
            <Button size="sm" icon={<Check className="size-3.5" />} onClick={() => act('acknowledge', [...sel])}>Acknowledge {sel.size}</Button>
            <Button size="sm" variant="primary" icon={<CheckCheck className="size-3.5" />} onClick={() => act('resolve', [...sel])}>Resolve {sel.size}</Button>
          </div>
        )}
      </div>
      {!data ? <div className="p-4"><Skeleton className="h-64" /></div> : data.length === 0 ? (
        <Empty icon={<CheckCheck className="size-5" />} title="Nothing to review">No alerts match these filters.</Empty>
      ) : (
        <Table>
          <thead><tr>
            {can.write && <th className="w-8"><input type="checkbox" aria-label="Select all" checked={sel.size === data.length} onChange={(e) => setSel(e.target.checked ? new Set(data.map((a) => a.id)) : new Set())} /></th>}
            <th>Severity</th><th>Alert</th><th>Device</th><th className="hidden lg:table-cell">Rule</th><th>When</th><th>Status</th>{can.write && <th />}
          </tr></thead>
          <tbody>
            {data.map((a) => (
              <tr key={a.id} className="hover:bg-panel-2/60">
                {can.write && <td><input type="checkbox" aria-label="Select" checked={sel.has(a.id)} onChange={(e) => { const s = new Set(sel); if (e.target.checked) s.add(a.id); else s.delete(a.id); setSel(s) }} /></td>}
                <td><SeverityBadge severity={a.severity} /></td>
                <td><div className="font-medium">{a.title}</div>{a.details && <div className="max-w-md truncate text-xs text-muted">{a.details}</div>}</td>
                <td><Link to={`/devices/${a.deviceId}`} className="text-brand-ink hover:underline">{a.hostname}</Link></td>
                <td className="hidden text-xs text-fg-2 lg:table-cell">{a.rule}</td>
                <td className="whitespace-nowrap text-xs text-muted" title={dateTime(a.triggeredAt)}>{ago(a.triggeredAt)}</td>
                <td>
                  <Badge tone={a.status === 'open' ? 'critical' : a.status === 'acknowledged' ? 'warning' : 'good'}>{a.status}</Badge>
                  {a.handledBy && <div className="mt-0.5 text-[10px] text-muted">by {a.handledBy}</div>}
                </td>
                {can.write && (
                  <td className="whitespace-nowrap text-right">
                    {a.status === 'open' && <Button size="sm" variant="ghost" onClick={() => act('acknowledge', [a.id])}>Ack</Button>}
                    {a.status !== 'resolved' && <Button size="sm" variant="ghost" onClick={() => act('resolve', [a.id])}>Resolve</Button>}
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </Table>
      )}
    </Card>
  )
}

function Rules() {
  const { can } = useAuth()
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ['rules'], queryFn: () => api.get<Rule[]>('/api/alert-rules') })
  const [edit, setEdit] = useState<Partial<Rule> | null>(null)

  const save = async () => {
    if (!edit) return
    const body = { name: edit.name, type: edit.type, threshold: Number(edit.threshold ?? 0), parameter: edit.parameter, severity: edit.severity ?? 'warning', enabled: edit.enabled ?? true, notifyEmail: !!edit.notifyEmail, notifyWebhook: !!edit.notifyWebhook }
    try {
      if (edit.id) await api.put(`/api/alert-rules/${edit.id}`, body)
      else await api.post('/api/alert-rules', body)
      toast.success('Rule saved'); setEdit(null); qc.invalidateQueries({ queryKey: ['rules'] })
    } catch (e) { toast.error(e instanceof ApiError ? e.message : 'Failed') }
  }
  const toggle = async (r: Rule) => { await api.put(`/api/alert-rules/${r.id}`, { ...r, enabled: !r.enabled }); qc.invalidateQueries({ queryKey: ['rules'] }) }
  const t = edit?.type ? ruleTypes[edit.type] : undefined

  return (
    <>
      {can.super && <div className="mb-4 flex justify-end"><Button variant="primary" icon={<Plus className="size-4" />} onClick={() => setEdit({ type: 'dataUsageDaily', severity: 'warning', enabled: true, threshold: 2048 })}>New rule</Button></div>}
      <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-3">
        {data?.map((r) => (
          <Card key={r.id}>
            <div className="flex items-start justify-between gap-3">
              <div className="min-w-0">
                <div className="flex items-center gap-2"><SeverityBadge severity={r.severity} /><span className="truncate font-medium">{r.name}</span></div>
                <p className="mt-1.5 text-xs text-muted">{ruleTypes[r.type]?.label}{ruleTypes[r.type]?.threshold ? ` ${r.threshold} ${ruleTypes[r.type].threshold}` : ''}</p>
                {r.parameter && <p className="mt-1 truncate text-xs text-fg-2">{r.parameter}</p>}
                <div className="mt-2 flex gap-1.5">{r.notifyEmail && <Badge>Email</Badge>}{r.notifyWebhook && <Badge>Webhook</Badge>}<Badge>Console</Badge></div>
              </div>
              <Switch checked={r.enabled} onChange={() => can.super && toggle(r)} disabled={!can.super} />
            </div>
            {can.super && (
              <div className="mt-3 flex gap-1 border-t border-line pt-3">
                <Button size="sm" variant="ghost" icon={<Pencil className="size-3.5" />} onClick={() => setEdit(r)}>Edit</Button>
                <Button size="sm" variant="ghost" icon={<Trash2 className="size-3.5" />} onClick={async () => { if (await confirmDialog({ title: `Delete rule "${r.name}"?`, message: "This can't be undone.", tone: 'danger' })) { await api.del(`/api/alert-rules/${r.id}`); qc.invalidateQueries({ queryKey: ['rules'] }) } }}>Delete</Button>
              </div>
            )}
          </Card>
        ))}
      </div>
      <Modal open={!!edit} onClose={() => setEdit(null)} title={edit?.id ? 'Edit rule' : 'New alert rule'}
        footer={<><Button onClick={() => setEdit(null)}>Cancel</Button><Button variant="primary" onClick={save} disabled={!edit?.name}>Save rule</Button></>}>
        {edit && (
          <div className="space-y-3">
            <Field label="Name"><Input value={edit.name ?? ''} onChange={(e) => setEdit({ ...edit, name: e.target.value })} placeholder="e.g. Heavy data usage" /></Field>
            <Field label="Condition">
              <Select className="w-full" value={edit.type} onChange={(e) => setEdit({ ...edit, type: e.target.value })}>
                {Object.entries(ruleTypes).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
              </Select>
            </Field>
            {t?.threshold && <Field label="Threshold" hint={t.threshold}><Input type="number" value={edit.threshold ?? 0} onChange={(e) => setEdit({ ...edit, threshold: Number(e.target.value) })} /></Field>}
            {t?.parameter && <Field label="Match" hint={t.parameter}><Input value={edit.parameter ?? ''} onChange={(e) => setEdit({ ...edit, parameter: e.target.value })} /></Field>}
            <Field label="Severity">
              <Select className="w-full" value={edit.severity} onChange={(e) => setEdit({ ...edit, severity: e.target.value })}>
                <option value="info">Info</option><option value="warning">Warning</option><option value="critical">Critical</option>
              </Select>
            </Field>
            <div className="space-y-3 rounded-xl bg-panel-2 p-3">
              <Switch checked={edit.enabled ?? true} onChange={(v) => setEdit({ ...edit, enabled: v })} label="Enabled" />
              <Switch checked={!!edit.notifyEmail} onChange={(v) => setEdit({ ...edit, notifyEmail: v })} label="Email notification" description="Uses Notifications:Smtp settings on the server" />
              <Switch checked={!!edit.notifyWebhook} onChange={(v) => setEdit({ ...edit, notifyWebhook: v })} label="Teams / webhook notification" description="Uses Notifications:WebhookUrl" />
            </div>
          </div>
        )}
      </Modal>
    </>
  )
}
