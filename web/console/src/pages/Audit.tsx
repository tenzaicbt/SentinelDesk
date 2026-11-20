import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { ScrollText, Search } from 'lucide-react'
import { api, qs } from '../lib/api'
import { dateTime } from '../lib/format'
import { Badge, Card, Empty, Input, PageHeader, Select, Skeleton, Table, type Tone } from '../components/ui'

interface Entry { id: number; at: string; actor: string; action: string; target?: string; ip?: string; details?: string }

const tone = (a: string): Tone =>
  a.startsWith('auth.failed') || a.includes('mfaFailed') ? 'critical'
    : a.includes('uninstall') || a.includes('delete') || a.includes('purge') || a.includes('retire') ? 'warning'
      : a.startsWith('report') || a === 'device.view' ? 'neutral' : 'brand'

export default function Audit() {
  const [actor, setActor] = useState('')
  const [action, setAction] = useState('')
  const { data } = useQuery({ queryKey: ['audit', actor, action], queryFn: () => api.get<Entry[]>(`/api/audit${qs({ actor, action, limit: 1000 })}`) })
  return (
    <>
      <PageHeader title="Audit log" icon={<ScrollText className="size-5" />} subtitle="Every console action — sign-ins, device views, exports, policy changes, commands" />
      <Card pad={false}>
        <div className="flex flex-wrap gap-2 border-b border-line p-3">
          <div className="relative w-64"><Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted" /><Input value={actor} onChange={(e) => setActor(e.target.value)} placeholder="Actor…" className="pl-9" /></div>
          <Select value={action} onChange={(e) => setAction(e.target.value)}>
            <option value="">All actions</option>
            {['auth', 'device', 'policy', 'report', 'alert', 'user', 'enrollToken', 'release', 'org'].map((a) => <option key={a} value={a}>{a}</option>)}
          </Select>
        </div>
        {!data ? <div className="p-4"><Skeleton className="h-64" /></div> : data.length === 0 ? <Empty icon={<ScrollText className="size-5" />} title="No entries" /> : (
          <Table>
            <thead><tr><th>Time</th><th>Actor</th><th>Action</th><th>Target</th><th className="hidden md:table-cell">Details</th><th className="hidden lg:table-cell">IP</th></tr></thead>
            <tbody>
              {data.map((e) => (
                <tr key={e.id}>
                  <td className="whitespace-nowrap text-xs text-muted">{dateTime(e.at)}</td>
                  <td className="font-medium">{e.actor}</td>
                  <td><Badge tone={tone(e.action)}>{e.action}</Badge></td>
                  <td className="max-w-56 truncate text-fg-2">{e.target}</td>
                  <td className="hidden max-w-72 truncate text-xs text-muted md:table-cell">{e.details}</td>
                  <td className="hidden font-mono text-xs text-muted lg:table-cell">{e.ip}</td>
                </tr>
              ))}
            </tbody>
          </Table>
        )}
      </Card>
    </>
  )
}
