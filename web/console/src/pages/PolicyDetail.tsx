import { useEffect, useState, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { AppWindow, ArrowLeft, Ban, Building2, Clock, Copy, Globe, Monitor, Plus, Save, Settings2, Target, Trash2, Usb, X } from 'lucide-react'
import { api, ApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { ago } from '../lib/format'
import { Button, Callout, Card, cx, Empty, Field, Input, PageHeader, Segmented, Select, Skeleton, Switch } from '../components/ui'
import type { DeviceRow, OrgUnit } from './Devices'
import { confirmDialog } from '../components/dialog'
import { KIND_ICON } from '../components/policyIcons'
import { BLOCK_CATEGORIES, DAYS, DEFAULT_BLOCK, KINDS, NO_SCHEDULE, WORKING_HOURS, type BlockSchedule, type BlockSettings, type PolicyRow, type Settings } from '../lib/policy'
import { DurationPicker, fmtUntil, untilFrom, type Duration } from '../components/blocking'

export default function PolicyDetail() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const { data, isLoading } = useQuery({ queryKey: ['policies'], queryFn: () => api.get<PolicyRow[]>('/api/policies') })
  const p = data?.find((x) => x.id === id)
  return (
    <>
      <Link to="/policies" className="mb-3 inline-flex items-center gap-1 text-sm text-muted hover:text-fg"><ArrowLeft className="size-4" />All policies</Link>
      {isLoading ? <Skeleton className="h-96" /> : !p ? (
        <Empty title="Policy not found">It may have been deleted. <Link to="/policies" className="text-brand-ink hover:underline">Back to policies</Link></Empty>
      ) : (
        <>
          <PageHeader title={p.name} icon={KIND_ICON[p.kind]} subtitle={`${KINDS[p.kind].label}${p.description ? ` · ${p.description}` : ''}`} />
          <PolicyEditor key={p.id + p.version} p={p} onCopy={() => navigate(`/policies/new?from=${p.id}`)} onDeleted={() => navigate('/policies')} />
        </>
      )}
    </>
  )
}

function PolicyEditor({ p, onCopy, onDeleted }: { p: PolicyRow; onCopy: () => void; onDeleted: () => void }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const [name, setName] = useState(p.name)
  const [desc, setDesc] = useState(p.description ?? '')
  const [s, setS] = useState<Settings>(() => ({ ...structuredClone(p.settings), block: structuredClone(p.settings.block ?? DEFAULT_BLOCK) }))
  const [busy, setBusy] = useState(false)
  const original = JSON.stringify({ ...p.settings, block: p.settings.block ?? DEFAULT_BLOCK })
  const dirty = JSON.stringify(s) !== original || name !== p.name || desc !== (p.description ?? '')
  const ro = !can.super
  const b = s.block ?? DEFAULT_BLOCK
  const setB = (patch: Partial<BlockSettings>) => setS({ ...s, block: { ...b, ...patch } })

  const save = async () => {
    setBusy(true)
    try {
      await api.put(`/api/policies/${p.id}`, { name, description: desc, settings: s })
      toast.success('Saved. Online computers get the change within seconds.')
      qc.invalidateQueries({ queryKey: ['policies'] })
    } catch (e) { toast.error(e instanceof ApiError ? e.message : 'Could not save the policy') } finally { setBusy(false) }
  }
  const remove = async () => {
    if (!(await confirmDialog({ title: `Delete "${p.name}"?`, message: 'Its computers stop having these things blocked.', tone: 'danger' }))) return
    await api.del(`/api/policies/${p.id}`)
    qc.invalidateQueries({ queryKey: ['policies'] })
    onDeleted()
  }

  return (
    <div className="min-w-0 space-y-3">
      <Card>
        <div className="flex flex-wrap items-end gap-3">
          <div className="grid min-w-0 flex-1 gap-3 sm:grid-cols-2">
            <Field label="Policy name"><Input value={name} onChange={(e) => setName(e.target.value)} disabled={ro} /></Field>
            <Field label="Description (optional)"><Input value={desc} placeholder="Who is this for?" onChange={(e) => setDesc(e.target.value)} disabled={ro} /></Field>
          </div>
          {can.super && (
            <div className="flex gap-2">
              <Button variant="ghost" icon={<Copy className="size-4" />} onClick={onCopy}>Copy</Button>
              <Button variant="ghost" icon={<Trash2 className="size-4" />} onClick={remove} aria-label="Delete policy" />
              <Button variant="primary" icon={<Save className="size-4" />} loading={busy} disabled={!dirty} onClick={save}>Save</Button>
            </div>
          )}
        </div>
        <p className="mt-3 text-xs text-muted">
          {dirty
            ? <span className="font-medium text-amber-600 dark:text-warning">You have unsaved changes. Click Save to send them to the computers.</span>
            : <>Last changed {ago(p.updatedAt)}{p.updatedBy ? ` by ${p.updatedBy}` : ''}.</>}
        </p>
      </Card>

      {p.kind === 'websites' && <WebsitesSection b={b} setB={setB} ro={ro} />}
      {p.kind === 'apps' && <AppsSection b={b} setB={setB} ro={ro} />}
      {p.kind === 'usb' && <UsbSection b={b} setB={setB} ro={ro} />}
      <OptionsSection kind={p.kind} b={b} setB={setB} ro={ro} />
      <Assignments p={p} />
    </div>
  )
}

function WebsitesSection({ b, setB, ro }: { b: BlockSettings; setB: (patch: Partial<BlockSettings>) => void; ro: boolean }) {
  return (
    <Section icon={<Globe className="size-4" />} title="Websites to block" text="These websites will not open on the computers this policy applies to.">
      <ListInput values={b.urls} disabled={ro} onChange={(urls) => setB({ urls })}
        placeholder="Type a website, e.g. tiktok.com, and press Enter"
        help={<><code>tiktok.com</code> blocks the whole site. <code>youtube.com/shorts</code> blocks only that part.</>} />
      <div className="mt-5 border-t border-line pt-4">
        <p className="mb-2 text-sm font-medium">Or block a whole type of website</p>
        <div className="grid gap-1.5 sm:grid-cols-2 xl:grid-cols-4">
          {BLOCK_CATEGORIES.map((cat) => {
            const on = b.categories.includes(cat.name)
            return (
              <button key={cat.name} type="button" disabled={ro} aria-pressed={on}
                onClick={() => setB({ categories: on ? b.categories.filter((x) => x !== cat.name) : [...b.categories, cat.name] })}
                className={cx('flex items-start gap-2 rounded-lg border px-3 py-2 text-left transition disabled:opacity-60',
                  on ? 'border-critical/40 bg-critical/5' : 'border-line hover:border-line-strong')}>
                <span className={cx('mt-0.5 flex size-4 shrink-0 items-center justify-center rounded border', on ? 'border-critical bg-critical text-white' : 'border-line-strong')}>
                  {on && <Ban className="size-2.5" />}
                </span>
                <span className="min-w-0">
                  <span className={cx('block text-sm font-medium', on && 'text-critical')}>{cat.name}</span>
                  <span className="block truncate text-xs text-muted">{cat.examples}</span>
                </span>
              </button>
            )
          })}
        </div>
      </div>
      <p className="mt-4 text-xs text-muted">Chrome, Edge, Brave and Firefox show a "blocked by your organisation" page (Firefox after a restart). Other apps cannot reach the site.</p>
    </Section>
  )
}

function AppsSection({ b, setB, ro }: { b: BlockSettings; setB: (patch: Partial<BlockSettings>) => void; ro: boolean }) {
  return (
    <Section icon={<AppWindow className="size-4" />} title="Applications to block" text="These programs will not open on the computers this policy applies to. If one is already open, it is closed.">
      <ListInput values={b.apps} disabled={ro} onChange={(apps) => setB({ apps })}
        placeholder="Type a program name, e.g. steam.exe, and press Enter"
        help={<>Use the program's file name. Not sure? Open <b>Task Manager → Details</b> while it runs. Use <b>*</b> to match many, e.g. <code>*torrent*</code>.</>} />
      <div className="mt-5 space-y-3 border-t border-line pt-4">
        <Switch disabled={ro} checked={b.notifyUser} onChange={(notifyUser) => setB({ notifyUser })} label="Tell the user why" description="Shows a message when a blocked program is closed" />
        {b.notifyUser && <Input disabled={ro} maxLength={300} value={b.message} onChange={(e) => setB({ message: e.target.value })} aria-label="Message shown to the user" />}
      </div>
    </Section>
  )
}

function Section({ icon, title, text, children }: { icon: ReactNode; title: string; text?: string; children: ReactNode }) {
  return (
    <Card>
      <div className="mb-3 flex items-start gap-2.5">
        <span className="mt-0.5 text-muted">{icon}</span>
        <div>
          <h2 className="text-sm font-semibold">{title}</h2>
          {text && <p className="mt-0.5 text-xs text-muted">{text}</p>}
        </div>
      </div>
      {children}
    </Card>
  )
}

/** A list the user types into: Enter (or comma) adds, the × removes. */
function ListInput({ values, onChange, disabled, placeholder, help }: { values: string[]; onChange: (v: string[]) => void; disabled?: boolean; placeholder?: string; help?: ReactNode }) {
  const [v, setV] = useState('')
  const add = () => {
    const items = v.split(/[,\n]/).map((x) => x.trim()).filter((x) => x && !values.includes(x))
    if (items.length) onChange([...values, ...items])
    setV('')
  }
  return (
    <div>
      {!disabled && (
        <div className="flex gap-2">
          <Input value={v} placeholder={placeholder} onChange={(e) => setV(e.target.value)} className="flex-1"
            onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); add() } }} />
          <Button icon={<Plus className="size-4" />} disabled={!v.trim()} onClick={add}>Add</Button>
        </div>
      )}
      {help && <p className="mt-1.5 text-xs text-muted [&_code]:rounded [&_code]:bg-panel-2 [&_code]:px-1 [&_code]:font-mono">{help}</p>}
      {values.length > 0 ? (
        <ul className="mt-3 flex flex-wrap gap-1.5">
          {values.map((x) => (
            <li key={x} className="inline-flex items-center gap-1 rounded-md border border-line bg-panel-2 py-0.5 pr-1 pl-2 font-mono text-xs">
              {x}
              {!disabled && <button type="button" className="rounded p-0.5 text-muted hover:bg-panel hover:text-critical" onClick={() => onChange(values.filter((y) => y !== x))} aria-label={`Remove ${x}`}><X className="size-3" /></button>}
            </li>
          ))}
        </ul>
      ) : <p className="mt-3 text-xs text-muted italic">None yet</p>}
    </div>
  )
}

type TargetType = 'all' | 'orgUnit' | 'device'

/** Pick all computers, a branch/department or one computer. */
function TargetPicker({ type, setType, target, setTarget, exclude, allowAll = true }: {
  type: TargetType; setType: (t: TargetType) => void; target: string; setTarget: (t: string) => void; exclude: Set<string>; allowAll?: boolean
}) {
  const { data: org } = useQuery({ queryKey: ['org'], queryFn: () => api.get<OrgUnit[]>('/api/org') })
  const { data: devices } = useQuery({ queryKey: ['devices', '', ''], queryFn: () => api.get<DeviceRow[]>('/api/devices') })
  useEffect(() => setTarget(''), [type, setTarget])
  return (
    <>
      <Segmented value={type} onChange={setType}
        options={[...(allowAll ? [{ value: 'all' as const, label: 'All computers' }] : []), { value: 'orgUnit' as const, label: 'Branch or department' }, { value: 'device' as const, label: 'One computer' }]} />
      {type !== 'all' && (
        <Select value={target} onChange={(e) => setTarget(e.target.value)} className="min-w-56" aria-label="Choose computers">
          <option value="">Choose…</option>
          {type === 'orgUnit'
            ? org?.filter((o) => o.type !== 'organization' && !exclude.has(o.id)).sort((a, b) => a.name.localeCompare(b.name)).map((o) => <option key={o.id} value={o.id}>{o.name}</option>)
            : devices?.filter((d) => !exclude.has(d.id)).map((d) => <option key={d.id} value={d.id}>{d.hostname}{d.user ? ` · ${d.user}` : ''}</option>)}
        </Select>
      )}
    </>
  )
}

function useRoot() {
  const { data: org } = useQuery({ queryKey: ['org'], queryFn: () => api.get<OrgUnit[]>('/api/org') })
  return org?.find((o) => o.type === 'organization')
}

function TargetChip({ icon, name, until, extra, onRemove }: { icon: 'device' | 'unit'; name: ReactNode; until?: string; extra?: ReactNode; onRemove?: () => void }) {
  return (
    <li className="inline-flex items-center gap-2 rounded-lg border border-line bg-panel-2 px-2.5 py-1.5 text-sm">
      {icon === 'device' ? <Monitor className="size-3.5 text-muted" /> : <Building2 className="size-3.5 text-muted" />}
      {name}
      {extra}
      {until && <span className="inline-flex items-center gap-1 text-xs text-amber-600 dark:text-warning"><Clock className="size-3" />{fmtUntil(until)}</span>}
      {onRemove && <button onClick={onRemove} aria-label="Remove"><X className="size-3.5 text-muted hover:text-critical" /></button>}
    </li>
  )
}

function Assignments({ p }: { p: PolicyRow }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const root = useRoot()
  const [type, setType] = useState<TargetType>('orgUnit')
  const [target, setTarget] = useState('')
  const [dur, setDur] = useState<Duration>('always')
  const [date, setDate] = useState('')
  const already = new Set(p.assignments.map((a) => a.targetId))
  const onAll = !!root && already.has(root.id)
  const refresh = () => { qc.invalidateQueries({ queryKey: ['policies'] }); qc.invalidateQueries({ queryKey: ['org'] }) }

  const assign = async () => {
    const until = untilFrom(dur, date)
    if (until === undefined) { toast.error('Choose a date in the future'); return }
    const [targetType, targetId] = type === 'all' ? ['orgUnit', root?.id] : [type, target]
    if (!targetId) return
    try {
      await api.post(`/api/policies/${p.id}/assign`, { targetType, targetId, untilUtc: until })
      toast.success(until ? `Applied ${fmtUntil(until)}.` : 'Done. Those computers now have this policy.')
      setTarget(''); refresh()
    } catch (e) { toast.error(e instanceof ApiError ? e.message : 'Failed') }
  }
  const unassign = async (id: string) => { await api.del(`/api/policies/assignments/${id}`); refresh() }

  return (
    <Section icon={<Target className="size-4" />} title="Which computers"
      text="Apply it to all computers, a branch or department, or single computers, always or for a while. A computer can have several policies: all of them apply.">
      {p.assignments.length === 0 ? (
        <Callout tone="warning">Not applied to any computer yet, so it does nothing. Choose computers below.</Callout>
      ) : (
        <ul className="flex flex-wrap gap-2">
          {p.assignments.map((a) => (
            <TargetChip key={a.id} icon={a.targetType === 'device' ? 'device' : 'unit'} until={a.untilUtc}
              name={root && a.targetId === root.id ? <b>All computers</b> : (a.name ?? a.targetId)}
              onRemove={can.super ? () => unassign(a.id) : undefined} />
          ))}
        </ul>
      )}
      {can.super && (
        <div className="mt-4 flex flex-wrap items-center gap-2 border-t border-line pt-4">
          <TargetPicker type={type} setType={setType} target={target} setTarget={setTarget} exclude={already} />
          <DurationPicker value={dur} onChange={setDur} date={date} onDate={setDate} />
          <Button variant="primary" disabled={type === 'all' ? !root || onAll : !target} onClick={assign}>
            {type === 'all' && onAll ? 'Already on all computers' : 'Apply'}
          </Button>
        </div>
      )}
      <Exceptions p={p} />
    </Section>
  )
}

/** Computers or branches where this policy does not apply (always or for a while), and approved access requests. */
function Exceptions({ p }: { p: PolicyRow }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const [open, setOpen] = useState(false)
  const [type, setType] = useState<TargetType>('device')
  const [target, setTarget] = useState('')
  const [dur, setDur] = useState<Duration>('always')
  const [date, setDate] = useState('')
  const [value, setValue] = useState('')
  const list = p.exceptions ?? []
  const refresh = () => qc.invalidateQueries({ queryKey: ['policies'] })

  const add = async () => {
    const until = untilFrom(dur, date)
    if (until === undefined) { toast.error('Choose a date in the future'); return }
    try {
      await api.post(`/api/policies/${p.id}/exceptions`, { targetType: type, targetId: target, untilUtc: until, value: value.trim() || null })
      toast.success('Exception added. Those computers are updated within seconds.')
      setTarget(''); setValue(''); setOpen(false); refresh()
    } catch (e) { toast.error(e instanceof ApiError ? e.message : 'Failed') }
  }
  const remove = async (id: string) => { await api.del(`/api/policies/exceptions/${id}`); refresh() }

  return (
    <div className="mt-4 border-t border-line pt-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <h3 className="text-sm font-medium">Exceptions</h3>
          <p className="text-xs text-muted">Computers or branches where this policy does not apply, for example one manager's PC, or a temporary unblock.</p>
        </div>
        {can.super && !open && <Button size="sm" icon={<Plus className="size-3.5" />} onClick={() => setOpen(true)}>Add exception</Button>}
      </div>
      {list.length > 0 && (
        <ul className="mt-3 flex flex-wrap gap-2">
          {list.map((x) => (
            <TargetChip key={x.id} icon={x.targetType === 'device' ? 'device' : 'unit'} until={x.untilUtc} name={x.name ?? x.targetId}
              extra={<span className="text-xs text-muted">{x.value ? <>allows <b className="font-mono text-fg-2">{x.value}</b></> : 'not blocked'}{x.global ? ' (approved request)' : ''}</span>}
              onRemove={can.super ? () => remove(x.id) : undefined} />
          ))}
        </ul>
      )}
      {open && (
        <div className="mt-3 flex flex-wrap items-center gap-2 rounded-lg border border-line p-3">
          <TargetPicker type={type} setType={setType} target={target} setTarget={setTarget} exclude={new Set()} allowAll={false} />
          {p.kind !== 'usb' && (
            <Input className="w-56" value={value} onChange={(e) => setValue(e.target.value)}
              placeholder={p.kind === 'websites' ? 'Only this site (optional)' : 'Only this program (optional)'} aria-label="Only this item" />
          )}
          <DurationPicker value={dur} onChange={setDur} date={date} onDate={setDate} />
          <Button variant="primary" disabled={!target} onClick={add}>Add</Button>
          <Button variant="ghost" onClick={() => setOpen(false)}>Cancel</Button>
        </div>
      )}
    </div>
  )
}

// ───────────── options: what happens, when, who ─────────────

function OptionsSection({ kind, b, setB, ro }: { kind: PolicyRow['kind']; b: BlockSettings; setB: (patch: Partial<BlockSettings>) => void; ro: boolean }) {
  const sch = b.schedule ?? NO_SCHEDULE
  const users = b.users ?? []
  const setSch = (patch: Partial<BlockSchedule>) => setB({ schedule: { ...sch, ...patch } })
  return (
    <Section icon={<Settings2 className="size-4" />} title="Options" text="By default the policy blocks, all the time, for everyone.">
      <div className="grid gap-5 lg:grid-cols-3">
        {kind !== 'usb' ? (
          <Field label="What happens">
            <Segmented value={b.mode ?? 'block'} onChange={(v) => !ro && setB({ mode: v })}
              options={[{ value: 'block', label: 'Block' }, { value: 'warn', label: 'Warn' }, { value: 'log', label: 'Log only' }]} />
            <p className="mt-1.5 text-xs text-muted">
              {b.mode === 'warn' ? 'Allowed, but the user sees a warning and it is recorded.'
                : b.mode === 'log' ? 'Allowed and recorded only. Use it to see the effect before blocking.'
                : 'Stopped, and the attempt is recorded.'}
            </p>
          </Field>
        ) : (
          <Field label="USB drives">
            <Segmented value={b.usbMode ?? 'block'} onChange={(v) => !ro && setB({ usbMode: v })}
              options={[{ value: 'block', label: 'Block' }, { value: 'readOnly', label: 'Read-only' }]} />
            <p className="mt-1.5 text-xs text-muted">{b.usbMode === 'readOnly' ? 'Files can be opened but nothing can be copied to a drive.' : 'Drives cannot be used. Drives already plugged in are switched off.'}</p>
          </Field>
        )}
        <Field label="When">
          <Segmented value={sch.enabled ? 'times' : 'always'} onChange={(v) => !ro && setB({ schedule: v === 'always' ? { ...sch, enabled: false } : { ...WORKING_HOURS, ...sch, enabled: true } })}
            options={[{ value: 'always', label: 'Always' }, { value: 'times', label: 'Only at set times' }]} />
          {sch.enabled && (
            <div className="mt-2 space-y-2">
              <div className="flex flex-wrap gap-1">
                {DAYS.map((d) => {
                  const on = sch.days.includes(d)
                  return <button key={d} type="button" disabled={ro} aria-pressed={on} onClick={() => setSch({ days: on ? sch.days.filter((x) => x !== d) : DAYS.filter((x) => x === d || sch.days.includes(x)) })}
                    className={cx('rounded-md px-2 py-0.5 text-xs font-medium ring-1 ring-inset', on ? 'bg-brand-soft text-brand-ink ring-brand/40' : 'text-muted ring-line')}>{d}</button>
                })}
              </div>
              <div className="flex items-center gap-2 text-xs text-muted">
                from <Input type="time" disabled={ro} className="w-28" value={sch.start} onChange={(e) => setSch({ start: e.target.value })} />
                to <Input type="time" disabled={ro} className="w-28" value={sch.end} onChange={(e) => setSch({ end: e.target.value })} />
              </div>
              {!ro && <button type="button" className="text-xs text-brand-ink hover:underline" onClick={() => setB({ schedule: WORKING_HOURS })}>Working hours (Mon–Fri 08:30–17:00)</button>}
            </div>
          )}
        </Field>
        <Field label="Who">
          <Segmented value={users.length ? 'some' : 'all'} onChange={(v) => !ro && v === 'all' && setB({ users: [] })}
            options={[{ value: 'all', label: 'Everyone' }, { value: 'some', label: 'Only some users' }]} />
          <div className="mt-2">
            <ListInput values={users} disabled={ro} onChange={(u) => setB({ users: u })} placeholder="User or group, e.g. Cashiers"
              help={<>A user name (<code>nimal</code>, <code>OFFICE\nimal</code>) or a Windows group (<code>Cashiers</code>). Empty = everyone.</>} />
          </div>
        </Field>
      </div>
    </Section>
  )
}

// ───────────── USB: read-only and allowed drives ─────────────

function UsbSection({ b, setB, ro }: { b: BlockSettings; setB: (patch: Partial<BlockSettings>) => void; ro: boolean }) {
  const allow = b.usbAllowSerials ?? []
  const { data: recent } = useQuery({ queryKey: ['usb-recent'], queryFn: () => api.get<{ serial: string; model: string; lastHost: string; lastSeen: string }[]>('/api/usb/recent-drives') })
  const add = (serial: string) => !allow.includes(serial) && setB({ usbAllowSerials: [...allow, serial] })
  return (
    <Section icon={<Usb className="size-4" />} title="Allowed drives" text="Company-issued drives that always work, even where USB drives are blocked. Mice, keyboards and printers are never blocked.">
      <ListInput values={allow} disabled={ro} onChange={(v) => setB({ usbAllowSerials: v.map((x) => x.toUpperCase()) })} placeholder="Drive serial number, e.g. 0701ABCD1234"
        help="Find the serial on the computer's page (USB events), or pick a drive seen recently below." />
      {!ro && recent && recent.filter((r) => !allow.includes(r.serial)).length > 0 && (
        <div className="mt-4 border-t border-line pt-3">
          <p className="mb-2 text-xs font-medium text-fg-2">Drives seen in the last 30 days</p>
          <ul className="space-y-1">
            {recent.filter((r) => !allow.includes(r.serial)).slice(0, 8).map((r) => (
              <li key={r.serial} className="flex items-center gap-3 text-sm">
                <span className="min-w-0 flex-1 truncate">{r.model || 'USB drive'} <span className="font-mono text-xs text-muted">{r.serial}</span></span>
                <span className="text-xs text-muted">{r.lastHost} · {ago(r.lastSeen)}</span>
                <Button size="sm" icon={<Plus className="size-3.5" />} onClick={() => add(r.serial)}>Allow</Button>
              </li>
            ))}
          </ul>
        </div>
      )}
    </Section>
  )
}
