import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Circle, CircleMarker, MapContainer, Polyline, TileLayer, Tooltip, useMap } from 'react-leaflet'
import 'leaflet/dist/leaflet.css'
import { MapPin } from 'lucide-react'
import { api } from '../lib/api'
import { ago, dateTime } from '../lib/format'
import { Callout, Card, Empty, Kv, Segmented, Skeleton, Table } from './ui'
import { TILE_ATTRIBUTION, tileUrl } from './BranchMap'

export interface LocationInfo { enabled: boolean; lat?: number; lng?: number; accuracyM?: number; at?: string; source?: string; error?: string }
interface Point { time: string; lat: number; lng: number; accuracyM: number; source?: string }

/** Moves the view to the trail (or the current fix) whenever it changes. */
function FitTo({ points }: { points: [number, number][] }) {
  const map = useMap()
  const key = points.map((p) => p.join(',')).join(';')
  useEffect(() => {
    if (points.length === 1) map.setView(points[0], 15)
    else if (points.length > 1) map.fitBounds(points, { padding: [30, 30], maxZoom: 16 })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, map])
  return null
}

const mapsLink = (lat: number, lng: number) => `https://www.google.com/maps?q=${lat.toFixed(6)},${lng.toFixed(6)}`

/** Device location tab: current fix with accuracy radius, movement trail and recent positions. */
export function DeviceLocation({ id, loc, dark }: { id: string; loc?: LocationInfo | null; dark: boolean }) {
  const [hours, setHours] = useState<'24' | '168' | '720'>('24')
  const { data: trail } = useQuery({
    queryKey: ['locations', id, hours],
    queryFn: () => api.get<Point[]>(`/api/devices/${id}/locations?hours=${hours}`),
    refetchInterval: 60_000,
    enabled: !!loc,
  })

  if (!loc) return <Empty icon={<MapPin className="size-5" />} title="Location is not available for your role" />
  const has = loc.lat != null && loc.lng != null
  const pts: [number, number][] = (trail ?? []).map((p) => [p.lat, p.lng])
  const current: [number, number] | null = has ? [loc.lat!, loc.lng!] : null
  const fit = pts.length > 1 ? pts : current ? [current] : []

  return (
    <div className="space-y-3">
      {!loc.enabled && (
        <Callout tone="warning">
          Location tracking is off for this device. Turn on <b>Device location</b> in the device's policy (Policies → Collectors).
          {has && ' The last known position is shown below.'}
        </Callout>
      )}
      {loc.enabled && !has && (
        <Callout tone="warning">No position received yet. {loc.error ?? 'It arrives within a few minutes of a user signing in.'}</Callout>
      )}
      {loc.enabled && has && loc.error && <Callout tone="warning">{loc.error}</Callout>}

      <div className="grid gap-3 xl:grid-cols-[1fr_320px]">
        <Card pad={false} title="Location" subtitle="Windows Location (Wi-Fi / GPS). The dashed circle is the accuracy radius."
          actions={<Segmented value={hours} onChange={setHours} options={[{ value: '24', label: '24 h' }, { value: '168', label: '7 days' }, { value: '720', label: '30 days' }]} />}>
          {!current && pts.length === 0 ? (
            <Empty icon={<MapPin className="size-5" />} title="No location recorded" />
          ) : (
            <MapContainer center={current ?? pts[pts.length - 1]} zoom={15} scrollWheelZoom={false} style={{ height: 420, width: '100%' }}>
              <TileLayer key={dark ? 'd' : 'l'} url={tileUrl(dark)} maxNativeZoom={16} maxZoom={19}
                referrerPolicy="strict-origin-when-cross-origin" attribution={TILE_ATTRIBUTION} />
              <FitTo points={fit} />
              {pts.length > 1 && <Polyline positions={pts} pathOptions={{ color: 'var(--series-1)', weight: 3, opacity: 0.8 }} />}
              {(trail ?? []).slice(0, -1).map((p) => (
                <CircleMarker key={p.time} center={[p.lat, p.lng]} radius={3} pathOptions={{ color: 'var(--series-1)', weight: 1, fillOpacity: 0.7 }}>
                  <Tooltip>{dateTime(p.time)} · ±{Math.round(p.accuracyM)} m</Tooltip>
                </CircleMarker>
              ))}
              {current && (
                <>
                  {(loc.accuracyM ?? 0) > 0 && (
                    <Circle center={current} radius={loc.accuracyM!} interactive={false}
                      pathOptions={{ color: 'var(--series-2)', weight: 1, dashArray: '4 4', fillOpacity: 0.1 }} />
                  )}
                  <CircleMarker center={current} radius={8} pathOptions={{ color: dark ? '#0a0a0a' : '#ffffff', weight: 2, fillColor: 'var(--brand)', fillOpacity: 1 }}>
                    <Tooltip permanent direction="top" offset={[0, -8]}>Now · ±{Math.round(loc.accuracyM ?? 0)} m</Tooltip>
                  </CircleMarker>
                </>
              )}
            </MapContainer>
          )}
        </Card>

        <div className="space-y-3">
          <Card title="Current position">
            <Kv items={[
              ['Updated', loc.at ? <span title={dateTime(loc.at)}>{ago(loc.at)}</span> : '—'],
              ['Accuracy', loc.accuracyM != null ? `± ${Math.round(loc.accuracyM)} m` : '—'],
              ['Source', loc.source ?? '—'],
              ['Coordinates', has ? <a className="font-mono text-xs text-brand-ink hover:underline" href={mapsLink(loc.lat!, loc.lng!)} target="_blank" rel="noreferrer">{loc.lat!.toFixed(5)}, {loc.lng!.toFixed(5)}</a> : '—'],
            ]} />
          </Card>
          <Card pad={false} title="Recent positions">
            {!trail ? <div className="p-3"><Skeleton className="h-24" /></div> : trail.length === 0 ? <Empty title="None in this period" /> : (
              <div className="max-h-64 overflow-y-auto">
                <Table>
                  <thead><tr><th>Time</th><th className="text-right">Accuracy</th></tr></thead>
                  <tbody>
                    {[...trail].reverse().slice(0, 100).map((p) => (
                      <tr key={p.time}>
                        <td className="text-xs"><a className="hover:underline" href={mapsLink(p.lat, p.lng)} target="_blank" rel="noreferrer">{dateTime(p.time)}</a></td>
                        <td className="text-right text-xs tabular">± {Math.round(p.accuracyM)} m</td>
                      </tr>
                    ))}
                  </tbody>
                </Table>
              </div>
            )}
          </Card>
        </div>
      </div>
    </div>
  )
}
