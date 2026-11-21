import { useEffect, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { Braces, Building2, Clock, Copy, EyeOff, Monitor, Plus, Save, ShieldCheck, SlidersHorizontal, Trash2, Upload, X } from 'lucide-react'
import { api, ApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { ago } from '../lib/format'
import { Badge, Button, Callout, Card, cx, Empty, Field, Input, PageHeader, Segmented, Select, Skeleton, Switch } from '../components/ui'
import type { DeviceRow, OrgUnit } from './Devices'
import { confirmDialog, promptDialog } from '../components/dialog'

interface Settings {
  collectors: {
    heartbeatSec: number; performanceSec: number; appUsage: boolean; captureWindowTitles: 'full' | 'masked' | 'none'; idleThresholdSec: number
    bandwidthPerProcess: boolean; bandwidthPerAdapter: boolean; webDomains: boolean; usbLog: boolean; usbBlock: boolean
    inventory: boolean; security: boolean; processes: boolean; sessions: boolean
  }
  workingHours: { timeZone: string; days: string; start: string; end: string; collectOutsideHours: boolean }
  upload: { intervalSec: number; offlineMaxMb: number; offlineMaxDays: number }
  privacy: { excludeApps: string[]; excludeDomains: string[] }
  tray: { contactIt: string; monitoringNotice: string }
}
interface PolicyRow { id: string; name: string; description?: string; version: number; isDefault: boolean; updatedAt: string; updatedBy?: string; settings: Settings; assignments: { id: string; targetType: string; targetId: string; name?: string }[] }

export default function Policies() {
  const { can } = useAuth()
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ['policies'], queryFn: () => api.get<PolicyRow[]>('/api/policies') })
  const [selId, setSelId] = useState<string | null>(null)
  const sel = data?.find((p) => p.id === selId) ?? data?.[0]

  const create = async (from?: PolicyRow) => {
    const name = await promptDialog({
      title: from ? `Duplicate "${from.name}"` : 'New policy', label: 'Policy name', maxLength: 100,
      defaultValue: from ? `${from.name} (copy)` : 'New policy', confirmLabel: from ? 'Duplicate' : 'Create',
    })
    if (!name) return
    const base = from?.settings ?? data?.find((p) => p.isDefault)?.settings
    const r = await api.post<{ id: string }>('/api/policies', { name, description: from?.description, settings: base })
    await qc.invalidateQueries({ queryKey: ['policies'] })
    setSelId(r.id)
  }

  return (
    <>
      <PageHeader title="Policies" icon={<SlidersHorizontal className="size-5" />}
        subtitle="Signed configuration pushed to agents in real time · most specific assignment wins (device → department → branch → region → default)"
        actions={can.super && <Button variant="primary" icon={<Plus className="size-4" />} onClick={() => create()}>New policy</Button>} />
      {!data ? <Skeleton className="h-96" /> : (
        <div className="grid gap-3 lg:grid-cols-[280px_1fr]">
          <div className="space-y-2">
            {data.map((p) => (
              <button key={p.id} onClick={() => setSelId(p.id)}
                className={cx('w-full rounded-xl border p-3.5 text-left transition', sel?.id === p.id ? 'border-brand/50 bg-brand-soft' : 'border-line bg-panel hover:border-line-strong')}>
                <div className="flex items-center justify-between gap-2">
                  <span className="truncate font-medium">{p.name}</span>
                  {p.isDefault ? <Badge tone="brand">Default</Badge> : <Badge>{p.assignments.length} targets</Badge>}
                </div>
                <div className="mt-1 text-xs text-muted">v{p.version} · updated {ago(p.updatedAt)}</div>
              </button>
            ))}
          </div>
          {sel && <PolicyEditor key={sel.id + sel.version} p={sel} onDuplicate={() => create(sel)} onDeleted={() => setSelId(null)} />}
        </div>
      )}
    </>
  )
}

function PolicyEditor({ p, onDuplicate, onDeleted }: { p: PolicyRow; onDuplicate: () => void; onDeleted: () => void }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const [name, setName] = useState(p.name)
  const [desc, setDesc] = useState(p.description ?? '')
  const [s, setS] = useState<Settings>(structuredClone(p.settings))
  const [json, setJson] = useState(false)
  const [busy, setBusy] = useState(false)
  const dirty = JSON.stringify(s) !== JSON.stringify(p.settings) || name !== p.name || desc !== (p.description ?? '')
  const ro = !can.super
  const c = s.collectors
  const setC = (patch: Partial<Settings['collectors']>) => setS({ ...s, collectors: { ...c, ...patch } })

  const save = async () => {
    setBusy(true)
    try {
      await api.put(`/api/policies/${p.id}`, { name, description: desc, settings: s })
      toast.success('Policy saved, signed and pushed to online agents')
      qc.invalidateQueries({ queryKey: ['policies'] })
    } catch (e) { toast.error(e instanceof ApiError ? e.message : 'Failed') } finally { setBusy(false) }
  }

  return (
    <div className="min-w-0 space-y-3">
      <Card>
        <div className="flex flex-wrap items-start gap-3">
          <div className="grid min-w-0 flex-1 gap-3 sm:grid-cols-2">
            <Field label="Policy name"><Input value={name} onChange={(e) => setName(e.target.value)} disabled={ro} /></Field>
            <Field label="Description"><Input value={desc} onChange={(e) => setDesc(e.target.value)} disabled={ro} /></Field>
          </div>
          <div className="flex gap-2 pt-5">
            <Button variant="ghost" icon={<Braces className="size-4" />} onClick={() => setJson((j) => !j)}>{json ? 'Form' : 'JSON'}</Button>
            {can.super && <Button variant="ghost" icon={<Copy className="size-4" />} onClick={onDuplicate}>Duplicate</Button>}
            {can.super && !p.isDefault && <Button variant="ghost" icon={<Trash2 className="size-4" />} onClick={async () => { if (await confirmDialog({ title: `Delete policy "${p.name}"?`, message: 'Assigned devices fall back to their parent policy.', tone: 'danger' })) { await api.del(`/api/policies/${p.id}`); qc.invalidateQueries({ queryKey: ['policies'] }); onDeleted() } }} />}
            {can.super && <Button variant="primary" icon={<Save className="size-4" />} loading={busy} disabled={!dirty} onClick={save}>Save & push</Button>}
          </div>
        </div>
        {dirty && <p className="mt-3 text-xs text-amber-600 dark:text-warning">Unsaved changes — agents receive the new version (ECDSA-signed) within seconds of saving.</p>}
      </Card>

      {json ? (
        <Card title="Policy document" subtitle="As delivered to agents (signature added server-side)">
          <pre className="max-h-[600px] overflow-auto rounded-xl bg-panel-2 p-4 font-mono text-xs leading-relaxed">{JSON.stringify({ name, ...s }, null, 2)}</pre>
        </Card>
      ) : (
        <div className="grid gap-3 xl:grid-cols-2">
          <Card title="Collectors" subtitle="What the agent gathers">
            <div className="space-y-3">
              <Switch disabled={ro} checked={c.appUsage} onChange={(v) => setC({ appUsage: v })} label="Application usage time" description="Foreground app & active/idle time (tray, user session)" />
              <Switch disabled={ro} checked={c.webDomains} onChange={(v) => setC({ webDomains: v })} label="Websites (domain level)" description="DNS-Client ETW — domains only, no page content" />
              <Switch disabled={ro} checked={c.bandwidthPerAdapter} onChange={(v) => setC({ bandwidthPerAdapter: v })} label="Bandwidth per adapter" description="LAN / Wi-Fi / Mobile / VPN totals" />
              <Switch disabled={ro} checked={c.bandwidthPerProcess} onChange={(v) => setC({ bandwidthPerProcess: v })} label="Bandwidth per application" description="Kernel network ETW" />
              <Switch disabled={ro} checked={c.inventory} onChange={(v) => setC({ inventory: v })} label="Hardware & software inventory" description="Daily + on change" />
              <Switch disabled={ro} checked={c.security} onChange={(v) => setC({ security: v })} label="Security posture" description="AV, firewall, BitLocker, patches" />
              <Switch disabled={ro} checked={c.processes} onChange={(v) => setC({ processes: v })} label="Process activity" description="Process starts + 5-minute running snapshot" />
              <Switch disabled={ro} checked={c.sessions} onChange={(v) => setC({ sessions: v })} label="Sign-in sessions" description="Logon, logoff, lock, unlock" />
            </div>
          </Card>

          <div className="space-y-3">
            <Card title={<span className="inline-flex items-center gap-1.5"><EyeOff className="size-3.5" />Privacy</span>} subtitle="Data minimisation (PDPA)">
              <div className="space-y-3">
                <Field label="Window titles">
                  <Segmented value={c.captureWindowTitles} onChange={(v) => !ro && setC({ captureWindowTitles: v })}
                    options={[{ value: 'none', label: 'Don’t capture' }, { value: 'masked', label: 'Masked' }, { value: 'full', label: 'Full' }]} />
                </Field>
                <p className="-mt-2 text-xs text-muted">Masked removes e-mail addresses and long numbers and truncates to 80 characters.</p>
                <TagInput label="Excluded applications" hint="Never tracked, e.g. password managers" values={s.privacy.excludeApps} disabled={ro} onChange={(v) => setS({ ...s, privacy: { ...s.privacy, excludeApps: v } })} />
                <TagInput label="Excluded domains" hint="Wildcards allowed, e.g. *.bank.lk (banking, health)" values={s.privacy.excludeDomains} disabled={ro} onChange={(v) => setS({ ...s, privacy: { ...s.privacy, excludeDomains: v } })} />
              </div>
            </Card>
            <Card title={<span className="inline-flex items-center gap-1.5"><Upload className="size-3.5" />USB storage</span>}>
              <div className="space-y-3">
                <Switch disabled={ro} checked={c.usbLog} onChange={(v) => setC({ usbLog: v })} label="Log removable storage" />
                <Switch disabled={ro} checked={c.usbBlock} onChange={(v) => setC({ usbBlock: v })} label="Block USB mass storage" description="Applies to newly connected devices (USBSTOR disabled)" />
              </div>
            </Card>
          </div>

          <Card title={<span className="inline-flex items-center gap-1.5"><Clock className="size-3.5" />Working hours</span>}>
            <div className="grid gap-3 sm:grid-cols-3">
              <Field label="Days"><Select className="w-full" disabled={ro} value={s.workingHours.days} onChange={(e) => setS({ ...s, workingHours: { ...s.workingHours, days: e.target.value } })}><option>Mon-Fri</option><option>Mon-Sat</option><option>All</option></Select></Field>
              <Field label="Start"><Input type="time" disabled={ro} value={s.workingHours.start} onChange={(e) => setS({ ...s, workingHours: { ...s.workingHours, start: e.target.value } })} /></Field>
              <Field label="End"><Input type="time" disabled={ro} value={s.workingHours.end} onChange={(e) => setS({ ...s, workingHours: { ...s.workingHours, end: e.target.value } })} /></Field>
            </div>
            <div className="mt-4"><Switch disabled={ro} checked={s.workingHours.collectOutsideHours} onChange={(v) => setS({ ...s, workingHours: { ...s.workingHours, collectOutsideHours: v } })}
              label="Collect user activity outside working hours" description="When off, app usage and websites are not collected outside these hours (health & inventory continue)" /></div>
          </Card>

          <Card title="Timing & offline buffer">
            <div className="grid gap-3 sm:grid-cols-2">
              <NumField label="Heartbeat (s)" value={c.heartbeatSec} min={10} max={600} disabled={ro} onChange={(v) => setC({ heartbeatSec: v })} />
              <NumField label="Performance sample (s)" value={c.performanceSec} min={5} max={300} disabled={ro} onChange={(v) => setC({ performanceSec: v })} />
              <NumField label="Idle after (s)" value={c.idleThresholdSec} min={60} max={3600} disabled={ro} onChange={(v) => setC({ idleThresholdSec: v })} />
              <NumField label="Upload interval (s)" value={s.upload.intervalSec} min={15} max={3600} disabled={ro} onChange={(v) => setS({ ...s, upload: { ...s.upload, intervalSec: v } })} />
              <NumField label="Offline buffer (MB)" value={s.upload.offlineMaxMb} min={10} max={2000} disabled={ro} onChange={(v) => setS({ ...s, upload: { ...s.upload, offlineMaxMb: v } })} />
              <NumField label="Offline buffer (days)" value={s.upload.offlineMaxDays} min={1} max={60} disabled={ro} onChange={(v) => setS({ ...s, upload: { ...s.upload, offlineMaxDays: v } })} />
            </div>
          </Card>

          <Card className="xl:col-span-2" title={<span className="inline-flex items-center gap-1.5"><ShieldCheck className="size-3.5" />Tray transparency notice</span>} subtitle="Shown to users from the tray icon">
            <div className="grid gap-3 md:grid-cols-[1fr_2fr]">
              <Field label="Support contact"><Input disabled={ro} value={s.tray.contactIt} onChange={(e) => setS({ ...s, tray: { ...s.tray, contactIt: e.target.value } })} /></Field>
              <Field label="Monitoring notice">
                <textarea disabled={ro} rows={3} value={s.tray.monitoringNotice} onChange={(e) => setS({ ...s, tray: { ...s.tray, monitoringNotice: e.target.value } })}
                  className="w-full rounded-lg border border-line-strong bg-panel px-3 py-2 text-sm focus:border-brand focus:outline-none focus:ring-3 focus:ring-brand-soft" />
              </Field>
            </div>
          </Card>
        </div>
      )}
      <Assignments p={p} />
    </div>
  )
}

function NumField({ label, value, onChange, min, max, disabled }: { label: string; value: number; onChange: (v: number) => void; min: number; max: number; disabled?: boolean }) {
  return <Field label={label} hint={`${min}–${max}`}><Input type="number" min={min} max={max} value={value} disabled={disabled} onChange={(e) => onChange(Math.max(min, Math.min(max, Number(e.target.value) || min)))} /></Field>
}

function TagInput({ label, hint, values, onChange, disabled }: { label: string; hint?: string; values: string[]; onChange: (v: string[]) => void; disabled?: boolean }) {
  const [v, setV] = useState('')
  const add = () => { const t = v.trim(); if (t && !values.includes(t)) onChange([...values, t]); setV('') }
  return (
    <Field label={label} hint={hint}>
      <div className="flex min-h-9 flex-wrap items-center gap-1.5 rounded-lg border border-line-strong bg-panel px-2 py-1.5">
        {values.map((x) => (
          <span key={x} className="inline-flex items-center gap-1 rounded-md bg-panel-2 px-2 py-0.5 font-mono text-xs">
            {x}{!disabled && <button type="button" onClick={() => onChange(values.filter((y) => y !== x))} aria-label={`Remove ${x}`}><X className="size-3" /></button>}
          </span>
        ))}
        {!disabled && <input value={v} onChange={(e) => setV(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); add() } }} onBlur={add}
          className="min-w-24 flex-1 bg-transparent text-sm outline-none" placeholder="Add…" />}
      </div>
    </Field>
  )
}

function Assignments({ p }: { p: PolicyRow }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const { data: org } = useQuery({ queryKey: ['org'], queryFn: () => api.get<OrgUnit[]>('/api/org') })
  const { data: devices } = useQuery({ queryKey: ['devices', '', ''], queryFn: () => api.get<DeviceRow[]>('/api/devices') })
  const [type, setType] = useState<'orgUnit' | 'device'>('orgUnit')
  const [target, setTarget] = useState('')
  useEffect(() => setTarget(''), [type])
  const assign = async () => {
    await api.post(`/api/policies/${p.id}/assign`, { targetType: type, targetId: target })
    toast.success('Assigned — affected agents notified'); setTarget('')
    qc.invalidateQueries({ queryKey: ['policies'] }); qc.invalidateQueries({ queryKey: ['org'] })
  }
  return (
    <Card title="Assignments" subtitle={p.isDefault ? 'The default policy applies to every device without a more specific assignment.' : 'Where this policy applies'}>
      {p.assignments.length === 0 ? (
        p.isDefault ? <Callout>Applies organization-wide by default.</Callout> : <Empty title="Not assigned yet">Assign to a branch, department or individual device.</Empty>
      ) : (
        <ul className="flex flex-wrap gap-2">
          {p.assignments.map((a) => (
            <li key={a.id} className="inline-flex items-center gap-2 rounded-lg border border-line bg-panel-2 px-2.5 py-1.5 text-sm">
              {a.targetType === 'device' ? <Monitor className="size-3.5 text-muted" /> : <Building2 className="size-3.5 text-muted" />}
              {a.name ?? a.targetId}
              {can.super && <button onClick={async () => { await api.del(`/api/policies/assignments/${a.id}`); qc.invalidateQueries({ queryKey: ['policies'] }) }} aria-label="Remove"><X className="size-3.5 text-muted hover:text-critical" /></button>}
            </li>
          ))}
        </ul>
      )}
      {can.super && (
        <div className="mt-4 flex flex-wrap items-end gap-2 border-t border-line pt-4">
          <Segmented value={type} onChange={setType} options={[{ value: 'orgUnit', label: 'Branch / dept' }, { value: 'device', label: 'Device' }]} />
          <Select value={target} onChange={(e) => setTarget(e.target.value)} className="min-w-56">
            <option value="">Select…</option>
            {type === 'orgUnit'
              ? org?.filter((o) => o.type !== 'organization').sort((a, b) => a.name.localeCompare(b.name)).map((o) => <option key={o.id} value={o.id}>{o.name}{o.policy ? ` (currently ${o.policy.name})` : ''}</option>)
              : devices?.map((d) => <option key={d.id} value={d.id}>{d.hostname}</option>)}
          </Select>
          <Button variant="primary" disabled={!target} onClick={assign}>Assign</Button>
        </div>
      )}
    </Card>
  )
}
