import { useEffect, useState, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { AppWindow, ArrowLeft, Ban, Building2, Copy, Globe, Monitor, Plus, Save, Target, Trash2, Usb, X } from 'lucide-react'
import { api, ApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { ago } from '../lib/format'
import { Button, Callout, Card, cx, Empty, Field, Input, PageHeader, Segmented, Select, Skeleton, Switch } from '../components/ui'
import type { DeviceRow, OrgUnit } from './Devices'
import { confirmDialog } from '../components/dialog'
import { KIND_ICON } from '../components/policyIcons'
import { BLOCK_CATEGORIES, DEFAULT_BLOCK, KINDS, type BlockSettings, type PolicyRow, type Settings } from '../lib/policy'

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
      {p.kind === 'usb' && (
        <Section icon={<Usb className="size-4" />} title="USB drives are blocked" text="On every computer this policy applies to.">
          <p className="text-sm text-fg-2">Pen drives and external hard disks cannot be used. Mice, keyboards and printers still work.
            A drive that is already plugged in keeps working until it is removed.</p>
          <p className="mt-2 text-xs text-muted">To allow USB drives again, remove the computers below, or delete this policy.</p>
        </Section>
      )}
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

function Assignments({ p }: { p: PolicyRow }) {
  const { can } = useAuth()
  const qc = useQueryClient()
  const { data: org } = useQuery({ queryKey: ['org'], queryFn: () => api.get<OrgUnit[]>('/api/org') })
  const { data: devices } = useQuery({ queryKey: ['devices', '', ''], queryFn: () => api.get<DeviceRow[]>('/api/devices') })
  const [type, setType] = useState<TargetType>('orgUnit')
  const [target, setTarget] = useState('')
  useEffect(() => setTarget(''), [type])
  const root = org?.find((o) => o.type === 'organization')
  const already = new Set(p.assignments.map((a) => a.targetId))
  const onAll = !!root && already.has(root.id)

  const assign = async (targetType: 'orgUnit' | 'device', targetId: string) => {
    await api.post(`/api/policies/${p.id}/assign`, { targetType, targetId })
    toast.success('Done. Those computers now have this policy.'); setTarget('')
    qc.invalidateQueries({ queryKey: ['policies'] }); qc.invalidateQueries({ queryKey: ['org'] })
  }
  const unassign = async (id: string) => {
    await api.del(`/api/policies/assignments/${id}`)
    qc.invalidateQueries({ queryKey: ['policies'] }); qc.invalidateQueries({ queryKey: ['org'] })
  }

  return (
    <Section icon={<Target className="size-4" />} title="Which computers"
      text="Apply it to all computers, a branch or department, or single computers. A computer can have several policies: all of them apply.">
      {p.assignments.length === 0 ? (
        <Callout tone="warning">Not applied to any computer yet, so it does nothing. Choose computers below.</Callout>
      ) : (
        <ul className="flex flex-wrap gap-2">
          {p.assignments.map((a) => (
            <li key={a.id} className="inline-flex items-center gap-2 rounded-lg border border-line bg-panel-2 px-2.5 py-1.5 text-sm">
              {a.targetType === 'device' ? <Monitor className="size-3.5 text-muted" /> : <Building2 className="size-3.5 text-muted" />}
              {root && a.targetId === root.id ? <b>All computers</b> : (a.name ?? a.targetId)}
              {can.super && <button onClick={() => unassign(a.id)} aria-label={`Remove ${a.name ?? ''}`}><X className="size-3.5 text-muted hover:text-critical" /></button>}
            </li>
          ))}
        </ul>
      )}
      {can.super && (
        <div className="mt-4 flex flex-wrap items-end gap-2 border-t border-line pt-4">
          <Segmented value={type} onChange={setType}
            options={[{ value: 'all', label: 'All computers' }, { value: 'orgUnit', label: 'Branch or department' }, { value: 'device', label: 'One computer' }]} />
          {type === 'all' ? (
            <Button variant="primary" disabled={!root || onAll} onClick={() => root && assign('orgUnit', root.id)}>{onAll ? 'Already on all computers' : 'Apply to all computers'}</Button>
          ) : (
            <>
              <Select value={target} onChange={(e) => setTarget(e.target.value)} className="min-w-56" aria-label="Choose where to apply">
                <option value="">Choose…</option>
                {type === 'orgUnit'
                  ? org?.filter((o) => o.type !== 'organization' && !already.has(o.id)).sort((a, b) => a.name.localeCompare(b.name)).map((o) => <option key={o.id} value={o.id}>{o.name}</option>)
                  : devices?.filter((d) => !already.has(d.id)).map((d) => <option key={d.id} value={d.id}>{d.hostname}{d.user ? ` · ${d.user}` : ''}</option>)}
              </Select>
              <Button variant="primary" disabled={!target} onClick={() => assign(type, target)}>Apply</Button>
            </>
          )}
        </div>
      )}
    </Section>
  )
}
