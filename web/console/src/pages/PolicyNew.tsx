import { useState } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { ArrowLeft, FilePlus2 } from 'lucide-react'
import { api, ApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { Button, Callout, Card, cx, Field, Input, PageHeader, Skeleton } from '../components/ui'
import { KIND_ICON } from '../components/policyIcons'
import { DEFAULT_BLOCK, KIND_ORDER, KINDS, type PolicyKind, type PolicyRow, type Settings } from '../lib/policy'

const isKind = (v: string | null): v is PolicyKind => !!v && (KIND_ORDER as string[]).includes(v)

export default function PolicyNew() {
  const { can } = useAuth()
  const navigate = useNavigate()
  const qc = useQueryClient()
  const [params] = useSearchParams()
  const { data } = useQuery({ queryKey: ['policies'], queryFn: () => api.get<PolicyRow[]>('/api/policies') })
  const from = data?.find((p) => p.id === params.get('from'))
  const requested = params.get('kind')
  const [picked, setPicked] = useState<PolicyKind | null>(isKind(requested) ? requested : null)
  const kind = from?.kind ?? picked
  const [name, setName] = useState('')
  const [desc, setDesc] = useState('')
  const [busy, setBusy] = useState(false)

  const create = async () => {
    if (!kind) return
    const settings: Settings = from
      ? structuredClone(from.settings)
      : { collectors: { usbBlock: kind === 'usb' }, block: structuredClone(DEFAULT_BLOCK) }
    setBusy(true)
    try {
      const r = await api.post<{ id: string }>('/api/policies', { name: name.trim(), description: desc.trim() || undefined, kind, settings })
      await qc.invalidateQueries({ queryKey: ['policies'] })
      toast.success(kind === 'usb' ? 'Policy created. Now choose which computers it applies to.' : 'Policy created. Now add what to block and choose the computers.')
      navigate(`/policies/${r.id}`, { replace: true })
    } catch (e) { toast.error(e instanceof ApiError ? e.message : 'Could not create the policy') } finally { setBusy(false) }
  }

  return (
    <>
      <Link to="/policies" className="mb-3 inline-flex items-center gap-1 text-sm text-muted hover:text-fg"><ArrowLeft className="size-4" />All policies</Link>
      <PageHeader title={from ? `Copy "${from.name}"` : 'New policy'} icon={<FilePlus2 className="size-5" />}
        subtitle={from ? 'The copy starts with the same rules. You can change them on the next page.' : 'Choose what to block, then give the policy a name.'} />
      {!can.super ? <Callout tone="warning">Only administrators can create policies.</Callout> : !data ? <Skeleton className="h-72 max-w-3xl" /> : (
        <form className="max-w-3xl space-y-3" onSubmit={(e) => { e.preventDefault(); if (kind && name.trim() && !busy) create() }}>
          {!from && (
            <Card>
              <h2 className="mb-3 text-sm font-semibold">1. What do you want to block?</h2>
              <div className="grid gap-2 sm:grid-cols-3">
                {KIND_ORDER.map((k) => (
                  <button key={k} type="button" onClick={() => setPicked(k)} aria-pressed={picked === k}
                    className={cx('flex flex-col items-start gap-2 rounded-lg border p-3 text-left transition', picked === k ? 'border-brand/60 bg-brand-soft' : 'border-line hover:border-line-strong')}>
                    <span className={cx('grid size-9 place-items-center rounded-md [&_svg]:size-4.5', picked === k ? 'bg-brand text-white' : 'bg-panel-2 text-muted')}>{KIND_ICON[k]}</span>
                    <span className="text-sm font-medium">{KINDS[k].short}</span>
                    <span className="text-xs text-muted">{KINDS[k].text}</span>
                  </button>
                ))}
              </div>
            </Card>
          )}
          {kind && (
            <Card>
              <h2 className="mb-3 text-sm font-semibold">{from ? 'Name the copy' : '2. Give it a name'}</h2>
              <div className="grid gap-3 sm:grid-cols-2">
                <Field label="Name"><Input autoFocus required maxLength={100} value={name} placeholder={KINDS[kind].placeholder} onChange={(e) => setName(e.target.value)} /></Field>
                <Field label="Description (optional)"><Input maxLength={200} value={desc} placeholder="Who is this for?" onChange={(e) => setDesc(e.target.value)} /></Field>
              </div>
              <div className="mt-4 flex justify-end gap-2 border-t border-line pt-4">
                <Button type="button" variant="ghost" onClick={() => navigate('/policies')}>Cancel</Button>
                <Button type="submit" variant="primary" loading={busy} disabled={!name.trim()}>Create policy</Button>
              </div>
            </Card>
          )}
        </form>
      )}
    </>
  )
}
