import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { Pencil, Plus, ShieldCheck, Smartphone, Users as UsersIcon } from 'lucide-react'
import { api, ApiError } from '../lib/api'
import { ago } from '../lib/format'
import { Badge, Button, Card, Field, Input, Modal, PageHeader, Select, Skeleton, Switch, Table } from '../components/ui'
import type { OrgUnit } from './Devices'

interface UserRow { id: string; username: string; displayName: string; email?: string; role: string; orgUnitScope?: string; scope?: string; mfaEnabled: boolean; disabled: boolean; lastLoginAt?: string; locked: boolean }

const roles = {
  SuperAdmin: 'Full control of the system',
  BranchAdmin: 'Manage devices in their branch only',
  Auditor: 'Read-only, including audit log',
  HrViewer: 'Reports only — no websites, titles or live view',
}

export default function Users() {
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ['users'], queryFn: () => api.get<UserRow[]>('/api/users') })
  const { data: org } = useQuery({ queryKey: ['org'], queryFn: () => api.get<OrgUnit[]>('/api/org') })
  const [edit, setEdit] = useState<(Partial<UserRow> & { password?: string }) | null>(null)

  const save = async () => {
    if (!edit) return
    const body = { username: edit.username, displayName: edit.displayName, email: edit.email, role: edit.role, orgUnitScope: edit.orgUnitScope || null, password: edit.password || null, disabled: !!edit.disabled }
    try {
      if (edit.id) await api.put(`/api/users/${edit.id}`, body)
      else await api.post('/api/users', body)
      toast.success('User saved'); setEdit(null); qc.invalidateQueries({ queryKey: ['users'] })
    } catch (e) { toast.error(e instanceof ApiError ? e.message : 'Failed') }
  }

  return (
    <>
      <PageHeader title="Admin users" icon={<UsersIcon className="size-5" />} subtitle="Role-based access control with optional branch scoping"
        actions={<Button variant="primary" icon={<Plus className="size-4" />} onClick={() => setEdit({ role: 'BranchAdmin' })}>Add user</Button>} />
      <div className="mb-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {Object.entries(roles).map(([r, d]) => (
          <Card key={r}><Badge tone="brand">{r}</Badge><p className="mt-2 text-xs text-muted">{d}</p></Card>
        ))}
      </div>
      <Card pad={false}>
        {!data ? <div className="p-4"><Skeleton className="h-48" /></div> : (
          <Table>
            <thead><tr><th>User</th><th>Role</th><th>Scope</th><th>MFA</th><th>Last sign-in</th><th>Status</th><th /></tr></thead>
            <tbody>
              {data.map((u) => (
                <tr key={u.id}>
                  <td><div className="font-medium">{u.displayName}</div><div className="text-xs text-muted">{u.username}{u.email && ` · ${u.email}`}</div></td>
                  <td><Badge tone="brand">{u.role}</Badge></td>
                  <td className="text-fg-2">{u.scope ?? 'Entire organization'}</td>
                  <td>{u.mfaEnabled ? <Badge tone="good" icon={<ShieldCheck className="size-3" />}>On</Badge> : <Badge tone="warning">Off</Badge>}</td>
                  <td className="text-xs text-muted">{ago(u.lastLoginAt)}</td>
                  <td>{u.disabled ? <Badge>Disabled</Badge> : u.locked ? <Badge tone="critical">Locked</Badge> : <Badge tone="good">Active</Badge>}</td>
                  <td className="whitespace-nowrap text-right">
                    {u.mfaEnabled && <Button size="sm" variant="ghost" icon={<Smartphone className="size-3.5" />} onClick={async () => { if (confirm(`Reset MFA for ${u.username}?`)) { await api.post(`/api/users/${u.id}/reset-mfa`); qc.invalidateQueries({ queryKey: ['users'] }) } }}>Reset MFA</Button>}
                    <Button size="sm" variant="ghost" icon={<Pencil className="size-3.5" />} onClick={() => setEdit(u)}>Edit</Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </Table>
        )}
      </Card>
      <Modal open={!!edit} onClose={() => setEdit(null)} title={edit?.id ? `Edit ${edit.username}` : 'New admin user'}
        footer={<><Button onClick={() => setEdit(null)}>Cancel</Button><Button variant="primary" onClick={save} disabled={!edit?.username || !edit?.displayName}>Save</Button></>}>
        {edit && (
          <div className="space-y-3">
            <div className="grid grid-cols-2 gap-3">
              <Field label="Username"><Input value={edit.username ?? ''} disabled={!!edit.id} onChange={(e) => setEdit({ ...edit, username: e.target.value })} /></Field>
              <Field label="Display name"><Input value={edit.displayName ?? ''} onChange={(e) => setEdit({ ...edit, displayName: e.target.value })} /></Field>
            </div>
            <Field label="Email"><Input type="email" value={edit.email ?? ''} onChange={(e) => setEdit({ ...edit, email: e.target.value })} /></Field>
            <div className="grid grid-cols-2 gap-3">
              <Field label="Role">
                <Select className="w-full" value={edit.role} onChange={(e) => setEdit({ ...edit, role: e.target.value })}>
                  {Object.keys(roles).map((r) => <option key={r}>{r}</option>)}
                </Select>
              </Field>
              <Field label="Scope" hint="Restricts visible devices">
                <Select className="w-full" value={edit.orgUnitScope ?? ''} onChange={(e) => setEdit({ ...edit, orgUnitScope: e.target.value })}>
                  <option value="">Entire organization</option>
                  {org?.filter((o) => o.type !== 'organization').sort((a, b) => a.name.localeCompare(b.name)).map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
                </Select>
              </Field>
            </div>
            <Field label={edit.id ? 'Reset password (optional)' : 'Initial password'} hint="User must change it at first sign-in. Min 10 chars, upper, lower and a digit.">
              <Input type="password" value={edit.password ?? ''} onChange={(e) => setEdit({ ...edit, password: e.target.value })} autoComplete="new-password" />
            </Field>
            {edit.id && <Switch checked={!!edit.disabled} onChange={(v) => setEdit({ ...edit, disabled: v })} label="Disable account" />}
          </div>
        )}
      </Modal>
    </>
  )
}
