import { Fragment, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { Ban, ChevronDown, ChevronRight, Copy, Download, Eye, EyeOff, FlaskConical, KeyRound, Monitor, PackagePlus, Rocket, ShieldCheck, Terminal, Trash2, Upload } from 'lucide-react'
import { api, ApiError, downloadPost } from '../lib/api'
import { ago, bytes, dateTime } from '../lib/format'
import { Badge, Button, Callout, Card, cx, Empty, Field, Input, Kv, Modal, OnlineDot, PageHeader, Select, Skeleton, Switch, Table, Tabs } from '../components/ui'
import type { OrgUnit } from './Devices'
import { confirmDialog } from '../components/dialog'

interface Token { id: string; name: string; prefix: string; orgUnit?: string; expiresAt: string; usesLeft: number; usedCount: number; autoApprove: boolean; revoked: boolean; createdAt: string; createdBy?: string; active: boolean
  revealable: boolean; devices: number; activeDevices: number; onlineDevices: number }
interface TokenDevice { id: string; hostname: string; status: string; online: boolean; lastSeen?: string; user?: string; agentVersion: string; enrolledAt: string; orgUnit?: string; os: string }
interface Release { id: string; version: string; fileName: string; sha256: string; sizeBytes: number; rolloutPercent: number; notes?: string; uploadedAt: string; uploadedBy?: string }
interface ServerInfo { caThumbprint: string; caExpires: string; signingKeyFingerprint: string; encryptionKeyFingerprint: string; publicUrl?: string; requireMtls: boolean; demoMode: boolean }

export default function Agents() {
  const [tab, setTab] = useState<'enroll' | 'releases' | 'trust'>('enroll')
  return (
    <>
      <PageHeader title="Agent management" icon={<KeyRound className="size-5" />} subtitle="Enrollment, staged upgrades and trust configuration" />
      <Tabs value={tab} onChange={setTab} tabs={[
        { id: 'enroll', label: 'Enrollment', icon: <KeyRound className="size-3.5" /> },
        { id: 'releases', label: 'Releases & rollout', icon: <Rocket className="size-3.5" /> },
        { id: 'trust', label: 'Trust & keys', icon: <ShieldCheck className="size-3.5" /> },
      ]} />
      <div className="mt-3">{tab === 'enroll' ? <Enrollment /> : tab === 'releases' ? <Releases /> : <Trust />}</div>
    </>
  )
}

function CopyBlock({ text }: { text: string }) {
  return (
    <div className="group relative">
      <pre className="overflow-x-auto whitespace-pre-wrap break-all rounded-xl bg-zinc-950 p-4 pr-12 font-mono text-xs leading-relaxed text-zinc-100">{text}</pre>
      <button onClick={() => { navigator.clipboard.writeText(text); toast.success('Copied') }} className="absolute right-2 top-2 rounded-md p-1.5 text-zinc-400 hover:bg-white/10 hover:text-white" aria-label="Copy"><Copy className="size-4" /></button>
    </div>
  )
}

function Enrollment() {
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ['tokens'], queryFn: () => api.get<Token[]>('/api/agents/tokens') })
  const { data: org } = useQuery({ queryKey: ['org'], queryFn: () => api.get<OrgUnit[]>('/api/org') })
  const [open, setOpen] = useState(false)
  const [form, setForm] = useState({ name: '', orgUnitId: '', days: 30, uses: 500, autoApprove: true })
  const [created, setCreated] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<string | null>(null)
  const server = window.location.origin.replace(':5080', ':5443').replace(':5173', ':5443')

  const create = async () => {
    const r = await api.post<{ token: string }>('/api/agents/tokens', { ...form, orgUnitId: form.orgUnitId || null })
    setCreated(r.token); setOpen(false); qc.invalidateQueries({ queryKey: ['tokens'] })
  }

  return (
    <div className="space-y-3">
      <OfficeInstaller org={org} onCreated={() => qc.invalidateQueries({ queryKey: ['tokens'] })} />
      <div className="grid gap-3 xl:grid-cols-3">
        <Card className="xl:col-span-2" title="Manual / scripted deployment" subtitle="Elevated install (admin credentials) · works with GPO startup scripts, Intune Win32 apps or SCCM">
          <ol className="space-y-3 text-sm text-fg-2">
            <li><b className="text-fg">1.</b> Build the agent bundle: <code className="rounded bg-panel-2 px-1.5 font-mono text-xs">./build.ps1</code> → <code className="rounded bg-panel-2 px-1.5 font-mono text-xs">publish/agent</code></li>
            <li><b className="text-fg">2.</b> Create an enrollment token for the target branch (below).</li>
            <li><b className="text-fg">3.</b> Run as administrator on each PC (or push silently):</li>
          </ol>
          <div className="mt-3"><CopyBlock text={`powershell -ExecutionPolicy Bypass -File install-agent.ps1 -ServerUrl ${server} -EnrollToken <TOKEN> -UninstallKey "<organisation uninstall key>"`} /></div>
          <p className="mt-3 text-xs text-muted">Add <code className="font-mono">-ServerCertSha256 &lt;hash&gt;</code> to pin a private TLS certificate, or <code className="font-mono">-TrustOnFirstUse</code> for lab environments.</p>
        </Card>
        <Card title="Uninstall protection">
          <ul className="space-y-2.5 text-sm text-fg-2">
            <li className="flex gap-2"><ShieldCheck className="mt-0.5 size-4 shrink-0 text-good-ink" />Services ACL'd — only SYSTEM can stop or delete them</li>
            <li className="flex gap-2"><ShieldCheck className="mt-0.5 size-4 shrink-0 text-good-ink" />Uninstall needs admin rights <b>and</b> the Uninstall Key or a per-device one-time code</li>
            <li className="flex gap-2"><ShieldCheck className="mt-0.5 size-4 shrink-0 text-good-ink" />Guard ↔ Agent watchdog restarts each other</li>
            <li className="flex gap-2"><ShieldCheck className="mt-0.5 size-4 shrink-0 text-good-ink" />Stops, kills, binary changes and bad keys raise tamper alerts</li>
          </ul>
        </Card>
      </div>

      {created && (
        <Callout tone="good">
          <p className="font-medium">Token created. You can copy it again any time from the token list below.</p>
          <div className="mt-2"><CopyBlock text={created} /></div>
        </Callout>
      )}

      <Card pad={false} title="Enrollment tokens" actions={<Button variant="primary" size="sm" icon={<KeyRound className="size-3.5" />} onClick={() => setOpen(true)}>New token</Button>}>
        {!data ? <div className="p-4"><Skeleton className="h-40" /></div> : data.length === 0 ? <Empty icon={<KeyRound className="size-5" />} title="No tokens yet" /> : (
          <Table>
            <thead><tr><th className="w-6" /><th>Name</th><th>Token</th><th>Branch</th><th>Devices</th><th className="text-right">Used / left</th><th>Approval</th><th>Expires</th><th>Status</th><th /></tr></thead>
            <tbody>
              {data.map((t) => (
                <Fragment key={t.id}>
                  <tr className={cx(t.devices > 0 && 'cursor-pointer', expanded === t.id && 'bg-panel-2/50')}
                    onClick={() => t.devices > 0 && setExpanded(expanded === t.id ? null : t.id)}>
                    <td className="text-muted">{t.devices > 0 && (expanded === t.id ? <ChevronDown className="size-4" /> : <ChevronRight className="size-4" />)}</td>
                    <td className="font-medium">{t.name}<div className="text-xs font-normal text-muted">by {t.createdBy} · {ago(t.createdAt)}</div></td>
                    <td onClick={(e) => e.stopPropagation()}><TokenValue t={t} /></td>
                    <td className="text-fg-2">{t.orgUnit ?? 'Unassigned'}</td>
                    <td>
                      {t.devices === 0 ? <span className="text-xs text-muted">None</span> : (
                        <span className="inline-flex items-center gap-2 text-xs">
                          <span className="font-medium tabular text-fg">{t.devices}</span>
                          <span className="inline-flex items-center gap-1 text-good-ink"><OnlineDot online={t.onlineDevices > 0} />{t.onlineDevices} online</span>
                          {t.activeDevices < t.devices && <span className="text-muted">· {t.devices - t.activeDevices} not active</span>}
                        </span>
                      )}
                    </td>
                    <td className="text-right tabular">{t.usedCount} / {t.usesLeft}</td>
                    <td>{t.autoApprove ? <Badge>Automatic</Badge> : <Badge tone="warning">Manual</Badge>}</td>
                    <td className="text-xs text-muted">{dateTime(t.expiresAt)}</td>
                    <td>{t.active ? <Badge tone="good">Active</Badge> : <Badge>{t.revoked ? 'Revoked' : 'Expired'}</Badge>}</td>
                    <td onClick={(e) => e.stopPropagation()}>{t.active && <Button size="sm" variant="ghost" icon={<Ban className="size-3.5" />} onClick={async () => {
                      if (!(await confirmDialog({ title: `Revoke token "${t.name}"?`, message: 'No new devices can enroll with it. Devices already enrolled keep working.', confirmLabel: 'Revoke', tone: 'danger' }))) return
                      await api.post(`/api/agents/tokens/${t.id}/revoke`); qc.invalidateQueries({ queryKey: ['tokens'] })
                    }}>Revoke</Button>}</td>
                  </tr>
                  {expanded === t.id && (
                    <tr className="hover:bg-transparent!"><td colSpan={10} className="bg-panel-2/30 p-0!"><TokenDevices id={t.id} /></td></tr>
                  )}
                </Fragment>
              ))}
            </tbody>
          </Table>
        )}
      </Card>

      <Modal open={open} onClose={() => setOpen(false)} title="New enrollment token"
        footer={<><Button onClick={() => setOpen(false)}>Cancel</Button><Button variant="primary" disabled={!form.name} onClick={create}>Create token</Button></>}>
        <div className="space-y-3">
          <Field label="Name"><Input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder="e.g. Kandy branch rollout" autoFocus /></Field>
          <Field label="Place enrolled devices in">
            <Select className="w-full" value={form.orgUnitId} onChange={(e) => setForm({ ...form, orgUnitId: e.target.value })}>
              <option value="">Unassigned</option>
              {org?.filter((o) => o.type !== 'organization').sort((a, b) => a.name.localeCompare(b.name)).map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
            </Select>
          </Field>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Valid for (days)"><Input type="number" min={1} max={365} value={form.days} onChange={(e) => setForm({ ...form, days: Number(e.target.value) })} /></Field>
            <Field label="Max devices"><Input type="number" min={1} value={form.uses} onChange={(e) => setForm({ ...form, uses: Number(e.target.value) })} /></Field>
          </div>
          <Switch checked={form.autoApprove} onChange={(v) => setForm({ ...form, autoApprove: v })} label="Approve devices automatically" description="When off, new devices wait in “Pending approval” until an admin approves them" />
        </div>
      </Modal>
    </div>
  )
}

/** Token prefix with show / copy. The full token is decrypted server-side on demand (audited). */
function TokenValue({ t }: { t: Token }) {
  const [full, setFull] = useState<string | null>(null)
  const reveal = async () => {
    if (full) return full
    try { const r = await api.post<{ token: string }>(`/api/agents/tokens/${t.id}/reveal`); setFull(r.token); return r.token }
    catch (e) { toast.error(e instanceof ApiError ? e.message : 'Could not reveal token'); return null }
  }
  if (!t.revealable) return <span className="font-mono text-xs text-muted" title="Created before tokens could be revealed: only a hash is stored">{t.prefix}…</span>
  return (
    <span className="inline-flex items-center gap-1">
      <span className={cx('font-mono text-xs', full && 'select-all text-fg')}>{full ?? `${t.prefix}…`}</span>
      <button className="rounded p-1 text-muted hover:bg-panel-2 hover:text-fg" aria-label={full ? 'Hide token' : 'Show token'} title={full ? 'Hide' : 'Show'}
        onClick={async () => { if (full) setFull(null); else await reveal() }}>
        {full ? <EyeOff className="size-3.5" /> : <Eye className="size-3.5" />}
      </button>
      <button className="rounded p-1 text-muted hover:bg-panel-2 hover:text-fg" aria-label="Copy token" title="Copy"
        onClick={async () => { const v = await reveal(); if (v) { await navigator.clipboard.writeText(v); toast.success('Token copied', { description: 'Use it to enroll more PCs.' }) } }}>
        <Copy className="size-3.5" />
      </button>
    </span>
  )
}

/** Devices whose latest enrollment used this token, with live status. */
function TokenDevices({ id }: { id: string }) {
  const { data } = useQuery({ queryKey: ['token-devices', id], queryFn: () => api.get<TokenDevice[]>(`/api/agents/tokens/${id}/devices`), refetchInterval: 15_000 })
  if (!data) return <div className="p-3"><Skeleton className="h-16" /></div>
  if (data.length === 0) return <Empty icon={<Monitor className="size-4" />} title="No devices enrolled with this token" />
  const tone = (s: string) => (s === 'active' ? 'good' : s === 'pending' ? 'warning' : 'neutral') as 'good' | 'warning' | 'neutral'
  return (
    <div className="border-l-2 border-brand/40 pl-6">
      <Table>
        <thead><tr><th>Device</th><th>Status</th><th>Connection</th><th>User</th><th>Branch</th><th>Agent</th><th>Enrolled</th></tr></thead>
        <tbody>
          {data.map((d) => (
            <tr key={d.id}>
              <td><Link to={`/devices/${d.id}`} className="font-medium hover:text-brand-ink hover:underline">{d.hostname}</Link><div className="text-xs text-muted">{d.os}</div></td>
              <td><Badge tone={tone(d.status)}>{d.status}</Badge></td>
              <td><span className="inline-flex items-center gap-1.5 text-xs"><OnlineDot online={d.online} />{d.online ? 'Online' : d.lastSeen ? `Last seen ${ago(d.lastSeen)}` : 'Never connected'}</span></td>
              <td className="max-w-40 truncate text-xs text-fg-2">{d.user ?? '—'}</td>
              <td className="text-xs text-fg-2">{d.orgUnit ?? 'Unassigned'}</td>
              <td className="font-mono text-xs">{d.agentVersion}</td>
              <td className="text-xs text-muted">{ago(d.enrolledAt)}</td>
            </tr>
          ))}
        </tbody>
      </Table>
    </div>
  )
}

interface InstallerInfo { available: boolean; sizeBytes?: number; builtAt?: string; suggestions: string[] }

/** One file per branch: SentinelDesk-Setup-<Branch>.exe with server address + a fresh token embedded. */
function OfficeInstaller({ org, onCreated }: { org?: OrgUnit[]; onCreated: () => void }) {
  const { data: info } = useQuery({ queryKey: ['installer'], queryFn: () => api.get<InstallerInfo>('/api/agents/installer') })
  const [f, setF] = useState({ orgUnitId: '', serverUrl: '', uninstallKey: '', confirmKey: '', uses: 200, days: 30, autoApprove: true, trustOnFirstUse: true })
  const [busy, setBusy] = useState(false)
  const server = f.serverUrl || info?.suggestions[0] || ''
  const keyOk = f.uninstallKey.length >= 8 && f.uninstallKey === f.confirmKey

  const go = async () => {
    setBusy(true)
    try {
      const name = await downloadPost('/api/agents/installer', { ...f, serverUrl: server, orgUnitId: f.orgUnitId || null })
      toast.success(`${name} downloaded`, { description: 'Send this file to the office. It installs with administrator rights - no settings to type.' })
      onCreated()
    } catch (e) { toast.error(e instanceof ApiError ? e.message : 'Download failed') }
    finally { setBusy(false) }
  }

  return (
    <Card title={<span className="inline-flex items-center gap-1.5"><Download className="size-3.5" />Office installer — one file to send to each branch</span>}
      subtitle="Downloads SentinelDesk-Setup.exe with the server address, a new enrollment token and your uninstall key built in. Staff double-click it (admin rights) or admins push it silently with /quiet.">
      {!info ? <Skeleton className="h-40" /> : !info.available ? (
        <Callout tone="warning">The setup file has not been built yet. On the server run <code className="font-mono">.uild.ps1</code> - it creates <code className="font-mono">publish\SentinelDesk-Setup.exe</code>, then reload this page.</Callout>
      ) : (
        <div className="grid gap-3 lg:grid-cols-[1fr_1fr_auto]">
          <div className="space-y-3">
            <Field label="Branch / office" hint="New PCs are placed here automatically">
              <Select className="w-full" value={f.orgUnitId} onChange={(e) => setF({ ...f, orgUnitId: e.target.value })}>
                <option value="">Unassigned (place later)</option>
                {org?.filter((o) => o.type !== 'organization').sort((a, b) => a.name.localeCompare(b.name)).map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
              </Select>
            </Field>
            <Field label="Server address offices connect to" hint="Must be reachable from the branches (LAN/VPN/MPLS or internet) - not localhost">
              <Input list="sd-server-suggestions" value={server} onChange={(e) => setF({ ...f, serverUrl: e.target.value })} placeholder="https://monitor.company.lk:5443" />
              <datalist id="sd-server-suggestions">{info.suggestions.map((s) => <option key={s} value={s} />)}</datalist>
            </Field>
          </div>
          <div className="space-y-3">
            <div className="grid grid-cols-2 gap-3">
              <Field label="Uninstall key"><Input type="password" autoComplete="new-password" value={f.uninstallKey} onChange={(e) => setF({ ...f, uninstallKey: e.target.value })} /></Field>
              <Field label="Confirm key"><Input type="password" autoComplete="new-password" value={f.confirmKey} onChange={(e) => setF({ ...f, confirmKey: e.target.value })} /></Field>
            </div>
            <p className="-mt-1 text-[11px] text-muted">Only a hash is embedded. Keep the key safe - it is needed (with admin rights) to remove the agent.</p>
            <div className="grid grid-cols-2 gap-3">
              <Field label="Max PCs"><Input type="number" min={1} value={f.uses} onChange={(e) => setF({ ...f, uses: Number(e.target.value) })} /></Field>
              <Field label="Valid for (days)"><Input type="number" min={1} max={365} value={f.days} onChange={(e) => setF({ ...f, days: Number(e.target.value) })} /></Field>
            </div>
          </div>
          <div className="flex min-w-56 flex-col justify-between gap-3">
            <div className="space-y-3">
              <Switch checked={f.autoApprove} onChange={(v) => setF({ ...f, autoApprove: v })} label="Approve PCs automatically" />
              <Switch checked={f.trustOnFirstUse} onChange={(v) => setF({ ...f, trustOnFirstUse: v })} label="Trust server certificate on first connect" description="Needed for a self-signed / internal certificate" />
            </div>
            <div>
              <Button variant="primary" className="w-full" icon={<Download className="size-4" />} loading={busy} disabled={!server || !keyOk} onClick={go}>
                Download setup ({bytes(info.sizeBytes ?? 0)})
              </Button>
              {!keyOk && f.uninstallKey && <p className="mt-1 text-[11px] text-critical">Keys must match and be at least 8 characters</p>}
            </div>
          </div>
        </div>
      )}
    </Card>
  )
}

function Releases() {
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ['releases'], queryFn: () => api.get<{ releases: Release[]; versions: { version: string; count: number }[] }>('/api/agents/releases') })
  const [file, setFile] = useState<File | null>(null)
  const [version, setVersion] = useState('')
  const [notes, setNotes] = useState('')
  const [busy, setBusy] = useState(false)
  const total = data?.versions.reduce((s, v) => s + v.count, 0) ?? 0

  const upload = async () => {
    if (!file) return
    const fd = new FormData()
    fd.append('package', file); fd.append('version', version); fd.append('notes', notes)
    setBusy(true)
    try { await api.post('/api/agents/releases', fd); toast.success('Release uploaded and signed'); setFile(null); setVersion(''); setNotes(''); qc.invalidateQueries({ queryKey: ['releases'] }) }
    catch (e) { toast.error(e instanceof ApiError ? e.message : 'Upload failed') } finally { setBusy(false) }
  }
  const rollout = async (r: Release, pct: number) => {
    await api.post(`/api/agents/releases/${r.id}/rollout`, { percent: pct })
    toast.success(`v${r.version} rolling out to ${pct}% of devices`)
    qc.invalidateQueries({ queryKey: ['releases'] })
  }

  return (
    <div className="grid gap-3 xl:grid-cols-3">
      <Card title="Fleet versions" subtitle={`${total} active devices`}>
        <ul className="space-y-3">
          {data?.versions.map((v) => (
            <li key={v.version}>
              <div className="mb-1 flex justify-between text-sm"><span className="font-mono">v{v.version}</span><span className="tabular text-fg-2">{v.count} · {total ? Math.round((v.count / total) * 100) : 0}%</span></div>
              <div className="h-1.5 rounded-full bg-panel-2"><div className="h-full rounded-full bg-s1" style={{ width: `${total ? (v.count / total) * 100 : 0}%` }} /></div>
            </li>
          ))}
        </ul>
      </Card>
      <Card className="xl:col-span-2" title="Upload a release" subtitle="Zip of the agent publish folder. The server signs SHA-256 + version with its ECDSA key; agents and Guard verify before installing.">
        <div className="grid gap-3 sm:grid-cols-[1fr_140px]">
          <Field label="Package (.zip)"><Input type="file" accept=".zip" onChange={(e) => setFile(e.target.files?.[0] ?? null)} className="pt-1.5" /></Field>
          <Field label="Version"><Input value={version} onChange={(e) => setVersion(e.target.value)} placeholder="1.0.1" /></Field>
        </div>
        <Field label="Release notes" className="mt-3"><Input value={notes} onChange={(e) => setNotes(e.target.value)} /></Field>
        <Button className="mt-4" variant="primary" icon={<Upload className="size-4" />} disabled={!file || !version} loading={busy} onClick={upload}>Upload & sign</Button>
      </Card>
      <Card className="xl:col-span-3" pad={false} title="Releases" subtitle="Staged rollout: devices are bucketed deterministically, so raising the percentage only adds devices">
        {!data?.releases.length ? <Empty icon={<PackagePlus className="size-5" />} title="No releases uploaded" /> : (
          <Table>
            <thead><tr><th>Version</th><th>Package</th><th>Uploaded</th><th>Rollout</th><th /></tr></thead>
            <tbody>
              {data.releases.map((r) => (
                <tr key={r.id}>
                  <td className="font-mono font-medium">v{r.version}<div className="font-sans text-xs font-normal text-muted">{r.notes}</div></td>
                  <td className="text-xs"><div>{r.fileName} · {bytes(r.sizeBytes)}</div><div className="font-mono text-muted">sha256 {r.sha256.slice(0, 16)}…</div></td>
                  <td className="text-xs text-muted">{ago(r.uploadedAt)} by {r.uploadedBy}</td>
                  <td>
                    <div className="flex flex-wrap items-center gap-1">
                      {[0, 10, 50, 100].map((p) => (
                        <Button key={p} size="sm" variant={r.rolloutPercent === p ? 'primary' : 'secondary'} onClick={() => rollout(r, p)} icon={p === 10 ? <FlaskConical className="size-3" /> : undefined}>
                          {p === 0 ? 'Paused' : `${p}%`}
                        </Button>
                      ))}
                    </div>
                  </td>
                  <td><Button size="sm" variant="ghost" icon={<Trash2 className="size-3.5" />} onClick={async () => { if (await confirmDialog({ title: `Delete release ${r.version}?`, message: "The package is removed from the server. This can't be undone.", tone: 'danger' })) { await api.del(`/api/agents/releases/${r.id}`); qc.invalidateQueries({ queryKey: ['releases'] }) } }} /></td>
                </tr>
              ))}
            </tbody>
          </Table>
        )}
      </Card>
    </div>
  )
}

function Trust() {
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ['server-info'], queryFn: () => api.get<ServerInfo>('/api/agents/server-info') })
  if (!data) return <Skeleton className="h-64" />
  return (
    <div className="grid gap-3 lg:grid-cols-2">
      <Card title="Server key material" subtitle="Generated on first start; protected with DPAPI on the server">
        <Kv items={[
          ['Device CA thumbprint', <code className="font-mono text-xs">{data.caThumbprint}</code>],
          ['Device CA expires', dateTime(data.caExpires)],
          ['Policy/command signing key', <code className="font-mono text-xs">{data.signingKeyFingerprint}</code>],
          ['Payload encryption key', <code className="font-mono text-xs">{data.encryptionKeyFingerprint}</code>],
          ['Mutual TLS', data.requireMtls ? <Badge tone="good">Required</Badge> : <Badge tone="warning">Optional (HMAC enforced)</Badge>],
        ]} />
      </Card>
      <Card title="Encryption in this deployment">
        <ul className="space-y-2.5 text-sm text-fg-2">
          <li className="flex gap-2"><Terminal className="mt-0.5 size-4 shrink-0" />TLS 1.2/1.3 transport, per-device ECDSA P-256 client certificate (TPM-backed when available)</li>
          <li className="flex gap-2"><Terminal className="mt-0.5 size-4 shrink-0" />Every batch: AES-256-GCM with a fresh key wrapped by RSA-3072-OAEP</li>
          <li className="flex gap-2"><Terminal className="mt-0.5 size-4 shrink-0" />Offline queue on the PC: AES-256-GCM, key sealed with DPAPI (machine)</li>
          <li className="flex gap-2"><Terminal className="mt-0.5 size-4 shrink-0" />Policies, commands and update packages signed with ECDSA P-256</li>
          <li className="flex gap-2"><Terminal className="mt-0.5 size-4 shrink-0" />Per-request HMAC device tokens with replay protection</li>
        </ul>
      </Card>
      {data.demoMode && (
        <Card className="lg:col-span-2" title="Demo data">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <p className="text-sm text-fg-2">Demo mode is generating a simulated island-wide fleet. Remove it before production use (also set <code className="font-mono">Demo:Enabled=false</code>).</p>
            <Button variant="danger" icon={<Trash2 className="size-4" />} onClick={async () => {
              if (!(await confirmDialog({ title: 'Purge simulated devices?', message: 'All simulated devices and their data are deleted. Real devices are not affected.', confirmLabel: 'Purge', tone: 'danger' }))) return
              const r = await api.del<{ removed: number }>('/api/devices/simulated'); toast.success(`Removed ${r?.removed ?? 0} simulated devices`); qc.invalidateQueries()
            }}>Purge simulated devices</Button>
          </div>
        </Card>
      )}
    </div>
  )
}
