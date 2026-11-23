import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from 'react-router-dom'
import { ChevronRight, Plus, ShieldBan } from 'lucide-react'
import { api } from '../lib/api'
import { useAuth } from '../lib/auth'
import { ago } from '../lib/format'
import { Button, Card, cx, PageHeader, Skeleton } from '../components/ui'
import { KIND_ICON } from '../components/policyIcons'
import { KIND_ORDER, KINDS, summary, type PolicyKind, type PolicyRow } from '../lib/policy'

export default function Policies() {
  const { data } = useQuery({ queryKey: ['policies'], queryFn: () => api.get<PolicyRow[]>('/api/policies') })
  return (
    <>
      <PageHeader title="Policies" icon={<ShieldBan className="size-5" />}
        subtitle="Block websites, applications and USB drives on your computers. A computer gets every policy that applies to it." />
      {!data ? (
        <div className="grid gap-3 lg:grid-cols-3">{KIND_ORDER.map((k) => <Skeleton key={k} className="h-64" />)}</div>
      ) : (
        <div className="grid items-start gap-3 lg:grid-cols-3">
          {KIND_ORDER.map((k) => <KindColumn key={k} kind={k} policies={data.filter((p) => p.kind === k)} />)}
        </div>
      )}
    </>
  )
}

function KindColumn({ kind, policies }: { kind: PolicyKind; policies: PolicyRow[] }) {
  const { can } = useAuth()
  const navigate = useNavigate()
  return (
    <Card pad={false}>
      <div className="flex items-start gap-3 border-b border-line p-4">
        <span className="grid size-10 shrink-0 place-items-center rounded-lg bg-critical/10 text-critical">{KIND_ICON[kind]}</span>
        <div className="min-w-0 flex-1">
          <h2 className="font-semibold">{KINDS[kind].label}</h2>
          <p className="text-xs text-muted">{KINDS[kind].text}</p>
        </div>
      </div>
      {policies.length > 0 ? (
        <ul className="divide-y divide-line">
          {policies.map((p) => {
            const used = p.assignments.length > 0
            return (
              <li key={p.id}>
                <Link to={`/policies/${p.id}`} className="group flex items-center gap-3 px-4 py-3 hover:bg-panel-2">
                  <div className="min-w-0 flex-1">
                    <div className="truncate text-sm font-medium">{p.name}</div>
                    <div className="truncate text-xs text-fg-2">{summary(p)}</div>
                    <div className={cx('mt-0.5 truncate text-xs', used ? 'text-muted' : 'text-amber-600 dark:text-warning')}>
                      {used ? `On ${p.assignments.map((a) => a.name ?? 'Unknown').join(', ')}` : 'Not applied to any computer yet'} · {ago(p.updatedAt)}
                    </div>
                  </div>
                  <ChevronRight className="size-4 shrink-0 text-muted transition group-hover:translate-x-0.5 group-hover:text-fg" />
                </Link>
              </li>
            )
          })}
        </ul>
      ) : (
        <p className="px-4 py-6 text-center text-sm text-muted">No {KINDS[kind].short.toLowerCase()} are blocked yet.</p>
      )}
      {can.super && (
        <div className="border-t border-line p-3">
          <Button className="w-full" icon={<Plus className="size-4" />} onClick={() => navigate(`/policies/new?kind=${kind}`)}>New {KINDS[kind].label.toLowerCase()} policy</Button>
        </div>
      )}
    </Card>
  )
}
