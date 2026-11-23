import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { AlertTriangle, Check, Hand, ShieldOff, X } from 'lucide-react'
import { api, ApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { ago } from '../lib/format'
import { Badge, Button, Callout, Card, Field, Input, Modal, Segmented, Select } from './ui'
import type { OrgUnit } from '../pages/Devices'

// ───────────── durations ─────────────

export type Duration = 'always' | '1h' | 'today' | 'date'

/** The end time for a duration choice: null = until removed; undefined = no valid date chosen yet. */
export function untilFrom(d: Duration, date: string): string | null | undefined {
  if (d === 'always') return null
  if (d === '1h') return new Date(Date.now() + 3600_000).toISOString()
  if (d === 'today') { const t = new Date(); t.setHours(23, 59, 0, 0); return t.toISOString() }
  const t = date ? new Date(date) : null
  return t && !Number.isNaN(t.getTime()) && t.getTime() > Date.now() ? t.toISOString() : undefined
}

export function fmtUntil(iso?: string | null) {
  if (!iso) return ''
  const t = new Date(iso)
  const sameDay = t.toDateString() === new Date().toDateString()
  return `until ${t.toLocaleString(undefined, sameDay ? { hour: '2-digit', minute: '2-digit' } : { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })}`
}

export function DurationPicker({ value, onChange, date, onDate, label = 'For' }: {
  value: Duration; onChange: (d: Duration) => void; date: string; onDate: (v: string) => void; label?: string
}) {
  return (
    <div className="flex flex-wrap items-center gap-2">
      <span className="text-xs text-muted">{label}</span>
      <Select value={value} onChange={(e) => onChange(e.target.value as Duration)} aria-label={label}>
        <option value="always">Always</option>
        <option value="1h">1 hour</option>
        <option value="today">Until the end of today</option>
        <option value="date">Until a date…</option>
      </Select>
      {value === 'date' && <Input type="datetime-local" value={date} onChange={(e) => onDate(e.target.value)} className="w-52" aria-label="Until" />}
    </div>
  )
}

// ───────────── offline unlock code (device page) ─────────────

export function UnlockCodeDialog({ deviceId, hostname, open, onClose }: { deviceId: string; hostname: string; open: boolean; onClose: () => void }) {
  const [hours, setHours] = useState(1)
  const [res, setRes] = useState<{ code: string; hours: number; validUntil: string } | null>(null)
  const [busy, setBusy] = useState(false)
  const close = () => { setRes(null); onClose() }
  const create = async () => {
    setBusy(true)
    try { setRes(await api.post(`/api/devices/${deviceId}/unlock-code`, { hours })) }
    catch (e) { toast.error(e instanceof ApiError ? e.message : 'Could not create the code') } finally { setBusy(false) }
  }
  return (
    <Modal open={open} onClose={close} title="Unlock code (works offline)"
      footer={res ? <Button variant="primary" onClick={close}>Done</Button> : <><Button variant="ghost" onClick={close}>Cancel</Button><Button variant="primary" loading={busy} onClick={create}>Create code</Button></>}>
      {!res ? (
        <div className="space-y-3">
          <p className="text-sm text-fg-2">Give this code to the user of <b>{hostname}</b>, for example by phone. They type it in the tray
            (<i>Enter unlock code…</i>) and all blocking on that computer pauses. It works even when the computer is offline, and only once.</p>
          <Field label="Pause blocking for">
            <Segmented value={hours} onChange={setHours} options={[{ value: 1, label: '1 hour' }, { value: 4, label: '4 hours' }, { value: 8, label: '8 hours' }]} />
          </Field>
        </div>
      ) : (
        <div className="space-y-3">
          <div className="rounded-lg bg-panel-2 py-3 text-center font-mono text-2xl font-semibold tracking-[0.2em]">{res.code}</div>
          <p className="text-sm text-fg-2">Pauses blocking on {hostname} for {res.hours} hour{res.hours === 1 ? '' : 's'}. The code must be entered before
            {' '}{new Date(res.validUntil).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}. This is recorded in the audit log.</p>
        </div>
      )}
    </Modal>
  )
}

// ───────────── access requests (policies page) ─────────────

interface AccessRequestRow {
  id: string; deviceId: string; hostname?: string; user?: string; kind: 'websites' | 'apps' | 'usb'; value: string; reason?: string
  status: 'pending' | 'approved' | 'rejected'; createdAt: string; decidedBy?: string; grantUntilUtc?: string
}
const KIND_LABEL = { websites: 'Website', apps: 'Application', usb: 'USB drive' }

export function AccessRequests() {
  const { can } = useAuth()
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ['access-requests'], queryFn: () => api.get<AccessRequestRow[]>('/api/access-requests?status=pending'), refetchInterval: 20_000 })
  if (!data?.length) return null
  const decide = async (r: AccessRequestRow, action: 'approve' | 'reject', until?: string | null) => {
    try {
      await api.post(`/api/access-requests/${r.id}/${action}`, action === 'approve' ? { untilUtc: until } : {})
      toast.success(action === 'approve' ? `Allowed ${r.value} on ${r.hostname}` : 'Request declined')
      qc.invalidateQueries({ queryKey: ['access-requests'] }); qc.invalidateQueries({ queryKey: ['policies'] })
    } catch (e) { toast.error(e instanceof ApiError ? e.message : 'Failed') }
  }
  return (
    <Card className="mb-3 border-amber-500/40" title={<span className="inline-flex items-center gap-1.5"><Hand className="size-4 text-warning" />Access requests <Badge tone="warning">{data.length}</Badge></span>}
      subtitle="Users asked to use something that is blocked. Approving allows it on that computer only.">
      <ul className="divide-y divide-line">
        {data.map((r) => (
          <li key={r.id} className="flex flex-wrap items-center gap-3 py-2.5">
            <div className="min-w-0 flex-1">
              <div className="text-sm"><b className="font-mono">{r.value}</b> <span className="text-muted">· {KIND_LABEL[r.kind]}</span></div>
              <div className="text-xs text-muted">{r.user ?? 'A user'} on {r.hostname ?? 'a computer'} · {ago(r.createdAt)}</div>
              {r.reason && <div className="mt-0.5 text-xs text-fg-2">“{r.reason}”</div>}
            </div>
            {can.write && (
              <div className="flex flex-wrap gap-1.5">
                <Button size="sm" variant="primary" icon={<Check className="size-3.5" />} onClick={() => decide(r, 'approve', untilFrom('1h', ''))}>Allow 1 hour</Button>
                <Button size="sm" onClick={() => decide(r, 'approve', untilFrom('today', ''))}>Today</Button>
                <Button size="sm" onClick={() => decide(r, 'approve', null)}>Always</Button>
                <Button size="sm" variant="ghost" icon={<X className="size-3.5" />} onClick={() => decide(r, 'reject')}>Decline</Button>
              </div>
            )}
          </li>
        ))}
      </ul>
    </Card>
  )
}

// ───────────── emergency "Unblock all" (policies page) ─────────────

interface EmergencyRow { id: string; orgUnitId?: string; scope: string; untilUtc?: string; reason?: string; createdBy?: string; createdAt: string }

/** part = 'button': the Unblock all button and its dialog · part = 'banner': the notice while an emergency unblock is on. */
export function EmergencyUnblock({ part }: { part: 'button' | 'banner' }) {
  const { can, me } = useAuth()
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ['emergency'], queryFn: () => api.get<EmergencyRow[]>('/api/emergency'), enabled: can.super, refetchInterval: 30_000 })
  const { data: org } = useQuery({ queryKey: ['org'], queryFn: () => api.get<OrgUnit[]>('/api/org'), enabled: can.super })
  const [open, setOpen] = useState(false)
  const [scope, setScope] = useState('')
  const [dur, setDur] = useState<Duration>('1h')
  const [date, setDate] = useState('')
  const [reason, setReason] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [busy, setBusy] = useState(false)
  if (!can.super) return null
  const mfa = !!me?.mfaEnabled

  const start = async () => {
    const until = untilFrom(dur, date)
    if (until === undefined) { toast.error('Choose a date in the future'); return }
    setBusy(true)
    try {
      await api.post('/api/emergency', { orgUnitId: scope || null, untilUtc: until, reason, password, code: code || null })
      toast.success('Blocking is off. Online computers are unblocked within seconds.')
      setOpen(false); setPassword(''); setCode(''); setReason('')
      qc.invalidateQueries({ queryKey: ['emergency'] })
    } catch (e) { toast.error(e instanceof ApiError ? e.message : 'Failed') } finally { setBusy(false) }
  }
  const end = async (id: string) => {
    await api.del(`/api/emergency/${id}`)
    toast.success('Blocking is back on')
    qc.invalidateQueries({ queryKey: ['emergency'] })
  }

  if (part === 'banner') return (
    <>
      {data?.map((e) => (
        <Callout key={e.id} tone="critical" icon={<ShieldOff className="size-4" />}>
          <div className="flex flex-wrap items-center gap-3">
            <span className="flex-1"><b>Emergency unblock is on</b> for {e.scope}{e.untilUtc ? ` ${fmtUntil(e.untilUtc)}` : ' until turned off'}: nothing is blocked there.
              {' '}Started by {e.createdBy} {ago(e.createdAt)}{e.reason ? ` · “${e.reason}”` : ''}.</span>
            <Button size="sm" variant="primary" onClick={() => end(e.id)}>Turn blocking back on</Button>
          </div>
        </Callout>
      ))}
    </>
  )
  return (
    <>
      <Button variant="danger" icon={<AlertTriangle className="size-4" />} onClick={() => setOpen(true)}>Unblock all</Button>
      <Modal open={open} onClose={() => setOpen(false)} title="Emergency: unblock everything"
        footer={<><Button variant="ghost" onClick={() => setOpen(false)}>Cancel</Button><Button variant="danger" loading={busy} disabled={!password} onClick={start}>Unblock all</Button></>}>
        <div className="space-y-3">
          <p className="text-sm text-fg-2">Stops every block policy (websites, applications, USB) right away. Use it when blocking stops people from working.
            Policies are not changed and come back when you turn blocking back on.</p>
          <Field label="Where">
            <Select className="w-full" value={scope} onChange={(e) => setScope(e.target.value)}>
              <option value="">All computers</option>
              {org?.filter((o) => o.type !== 'organization').sort((a, b) => a.name.localeCompare(b.name)).map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
            </Select>
          </Field>
          <DurationPicker value={dur} onChange={setDur} date={date} onDate={setDate} label="For" />
          <Field label="Reason (saved in the audit log)"><Input value={reason} onChange={(e) => setReason(e.target.value)} placeholder="e.g. Payroll site wrongly blocked" /></Field>
          <Field label="Confirm with your password"><Input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" /></Field>
          {mfa && <Field label="MFA code"><Input inputMode="numeric" value={code} onChange={(e) => setCode(e.target.value)} placeholder="123456" /></Field>}
        </div>
      </Modal>
    </>
  )
}

