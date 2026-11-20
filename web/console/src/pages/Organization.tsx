import { useMemo, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { Building, Building2, ChevronDown, ChevronRight, Globe2, MapPin, Pencil, Plus, Trash2, Users } from 'lucide-react'
import { api, ApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { Badge, Button, Card, cx, Field, Input, Modal, PageHeader, Select, Skeleton } from '../components/ui'
import type { OrgUnit } from './Devices'

const icons: Record<string, typeof Building2> = { organization: Globe2, region: MapPin, branch: Building2, department: Users }

export default function Organization() {
  const { can } = useAuth()
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ['org'], queryFn: () => api.get<OrgUnit[]>('/api/org') })
  const [edit, setEdit] = useState<Partial<OrgUnit> | null>(null)
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set())

  const tree = useMemo(() => {
    const byParent = new Map<string | undefined, OrgUnit[]>()
    data?.forEach((u) => {
      const k = u.parentId && data.some((x) => x.id === u.parentId) ? u.parentId : undefined
      byParent.set(k, [...(byParent.get(k) ?? []), u])
    })
    byParent.forEach((v) => v.sort((a, b) => a.name.localeCompare(b.name)))
    return byParent
  }, [data])

  const totals = (id: string): { devices: number; online: number } => {
    const self = data?.find((u) => u.id === id)
    return (tree.get(id) ?? []).reduce((acc, c) => { const t = totals(c.id); return { devices: acc.devices + t.devices, online: acc.online + t.online } },
      { devices: self?.devices ?? 0, online: self?.online ?? 0 })
  }

  const save = async () => {
    if (!edit?.name) return
    const body = { name: edit.name, type: edit.type ?? 'branch', parentId: edit.parentId ?? null, lat: edit.lat ? Number(edit.lat) : null, lng: edit.lng ? Number(edit.lng) : null }
    try {
      if (edit.id) await api.put(`/api/org/${edit.id}`, body)
      else await api.post('/api/org', body)
      toast.success('Saved'); setEdit(null); qc.invalidateQueries({ queryKey: ['org'] })
    } catch (e) { toast.error(e instanceof ApiError ? e.message : 'Failed') }
  }

  const render = (u: OrgUnit, depth: number): React.ReactNode => {
    const kids = tree.get(u.id) ?? []
    const t = totals(u.id)
    const Icon = icons[u.type] ?? Building
    const open = !collapsed.has(u.id)
    return (
      <div key={u.id}>
        <div className="group flex items-center gap-2 border-b border-line py-2.5 pr-3 hover:bg-panel-2/60" style={{ paddingLeft: 12 + depth * 22 }}>
          <button className={cx('grid size-5 place-items-center text-muted', !kids.length && 'invisible')} onClick={() => {
            const s = new Set(collapsed); if (open) s.add(u.id); else s.delete(u.id); setCollapsed(s)
          }} aria-label={open ? 'Collapse' : 'Expand'}>{open ? <ChevronDown className="size-4" /> : <ChevronRight className="size-4" />}</button>
          <Icon className="size-4 text-muted" />
          <Link to={`/devices?orgUnitId=${u.id}`} className="font-medium hover:text-brand-ink">{u.name}</Link>
          <span className="text-xs capitalize text-muted">{u.type}</span>
          {u.policy && <Badge tone="brand">{u.policy.name}</Badge>}
          <span className="ml-auto text-xs tabular text-fg-2"><span className="font-medium text-fg">{t.online}</span>/{t.devices} online</span>
          {can.super && (
            <span className="flex gap-0.5 opacity-0 transition group-hover:opacity-100">
              <Button size="sm" variant="ghost" icon={<Plus className="size-3.5" />} onClick={() => setEdit({ parentId: u.id, type: u.type === 'organization' ? 'region' : u.type === 'region' ? 'branch' : 'department' })} aria-label="Add child" />
              <Button size="sm" variant="ghost" icon={<Pencil className="size-3.5" />} onClick={() => setEdit(u)} aria-label="Edit" />
              {u.type !== 'organization' && <Button size="sm" variant="ghost" icon={<Trash2 className="size-3.5" />} aria-label="Delete" onClick={async () => {
                if (!confirm(`Delete ${u.name}?`)) return
                try { await api.del(`/api/org/${u.id}`); qc.invalidateQueries({ queryKey: ['org'] }) } catch (e) { toast.error(e instanceof ApiError ? e.message : 'Failed') }
              }} />}
            </span>
          )}
        </div>
        {open && kids.map((k) => render(k, depth + 1))}
      </div>
    )
  }

  return (
    <>
      <PageHeader title="Organization" icon={<Building2 className="size-5" />} subtitle="Head office → regions → branches → departments. Drives scoping, policy inheritance and reports."
        actions={can.super && <Button variant="primary" icon={<Plus className="size-4" />} onClick={() => setEdit({ type: 'branch', parentId: data?.find((u) => u.type === 'organization')?.id })}>Add unit</Button>} />
      <Card pad={false}>
        {!data ? <div className="p-4"><Skeleton className="h-80" /></div> : (tree.get(undefined) ?? []).map((u) => render(u, 0))}
      </Card>
      <Modal open={!!edit} onClose={() => setEdit(null)} title={edit?.id ? 'Edit unit' : 'New unit'}
        footer={<><Button onClick={() => setEdit(null)}>Cancel</Button><Button variant="primary" onClick={save}>Save</Button></>}>
        {edit && (
          <div className="space-y-4">
            <Field label="Name"><Input value={edit.name ?? ''} onChange={(e) => setEdit({ ...edit, name: e.target.value })} autoFocus /></Field>
            <div className="grid grid-cols-2 gap-3">
              <Field label="Type">
                <Select className="w-full" value={edit.type} onChange={(e) => setEdit({ ...edit, type: e.target.value })} disabled={edit.type === 'organization'}>
                  <option value="region">Region / province</option><option value="branch">Branch</option><option value="department">Department</option>
                  {edit.type === 'organization' && <option value="organization">Organization</option>}
                </Select>
              </Field>
              <Field label="Parent">
                <Select className="w-full" value={edit.parentId ?? ''} onChange={(e) => setEdit({ ...edit, parentId: e.target.value })} disabled={edit.type === 'organization'}>
                  {data?.filter((u) => u.id !== edit.id).map((u) => <option key={u.id} value={u.id}>{u.name}</option>)}
                </Select>
              </Field>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <Field label="Latitude" hint="For the branch map (optional)"><Input type="number" step="0.0001" value={edit.lat ?? ''} onChange={(e) => setEdit({ ...edit, lat: e.target.value as unknown as number })} /></Field>
              <Field label="Longitude"><Input type="number" step="0.0001" value={edit.lng ?? ''} onChange={(e) => setEdit({ ...edit, lng: e.target.value as unknown as number })} /></Field>
            </div>
          </div>
        )}
      </Modal>
    </>
  )
}
