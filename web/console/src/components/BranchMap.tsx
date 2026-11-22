import { Fragment, useRef, useState } from 'react'
import { Circle, CircleMarker, MapContainer, TileLayer, Tooltip } from 'react-leaflet'
import 'leaflet/dist/leaflet.css'
import { ago, bytes } from '../lib/format'

export interface BranchPoint { id: string; name: string; lat: number; lng: number; total: number; online: number; todayBytes: number }
export interface DevicePoint { id: string; hostname: string; user?: string; branch?: string; lat: number; lng: number; accuracyM: number; at?: string; source?: string; online: boolean }

/**
 * Basemap tiles. Override at build time with VITE_MAP_TILE_URL (e.g. an internal tile server for intranet-only
 * deployments), or set it to "none" to always use the offline view.
 */
export const CUSTOM_TILES: string | undefined = import.meta.env.VITE_MAP_TILE_URL
export const tileUrl = (dark: boolean) =>
  CUSTOM_TILES || `https://server.arcgisonline.com/ArcGIS/rest/services/Canvas/World_${dark ? 'Dark' : 'Light'}_Gray_Base/MapServer/tile/{z}/{y}/{x}`
export const TILE_ATTRIBUTION: string = import.meta.env.VITE_MAP_TILE_ATTRIBUTION || 'Esri, HERE, Garmin, &copy; OpenStreetMap contributors'

/** Island map: marker size = device count, ring = share online (status color + tooltip text, never color alone). */
export function BranchMap({ branches, devices = [], dark, onSelect, onSelectDevice, height = 360 }: {
  branches: BranchPoint[]; devices?: DevicePoint[]; dark: boolean; onSelect?: (id: string) => void; onSelectDevice?: (id: string) => void; height?: number
}) {
  const [hover, setHover] = useState<string | null>(null)
  const max = Math.max(1, ...branches.map((b) => b.total))
  // Fall back to an offline view (no basemap, labelled markers) if tiles are blocked or there is no internet.
  const [offline, setOffline] = useState(CUSTOM_TILES === 'none')
  const stats = useRef({ ok: 0, err: 0 })

  return (
    <div className="relative">
      <MapContainer
        center={[7.75, 80.7]}
        zoom={7}
        minZoom={6}
        maxBounds={[[4.5, 78], [11, 83.5]]}
        scrollWheelZoom={false}
        style={{ height, width: '100%' }}
        attributionControl={!offline}
      >
        {!offline && (
          <TileLayer
            key={dark ? 'd' : 'l'}
            url={tileUrl(dark)}
            maxNativeZoom={16}
            // The console sends "Referrer-Policy: no-referrer"; tile servers require a Referer, so send the origin only.
            referrerPolicy="strict-origin-when-cross-origin"
            attribution={TILE_ATTRIBUTION}
            eventHandlers={{
              tileload: () => { stats.current.ok++ },
              tileerror: () => {
                stats.current.err++
                if (stats.current.err >= 4 && stats.current.ok === 0) setOffline(true)
              },
            }}
          />
        )}
        {branches.map((b) => {
          const ratio = b.total ? b.online / b.total : 0
          const color = ratio >= 0.8 ? 'var(--good)' : ratio >= 0.5 ? 'var(--warning)' : 'var(--critical)'
          return (
            <CircleMarker
              key={`${b.id}-${offline}`}
              center={[b.lat, b.lng]}
              radius={8 + (b.total / max) * 14}
              pathOptions={{ color, weight: 3, fillColor: 'var(--series-1)', fillOpacity: 0.55 }}
              eventHandlers={{ click: () => onSelect?.(b.id) }}
            >
              <Tooltip direction={offline ? 'right' : 'top'} offset={offline ? [10, 0] : [0, -8]} permanent={offline} className={offline ? 'sd-map-label' : undefined}>
                <div className="text-xs">
                  <div className="font-semibold">{b.name}</div>
                  {!offline && <div>{b.online}/{b.total} online · {bytes(b.todayBytes)} today</div>}
                  {offline && <div className="tabular">{b.online}/{b.total}</div>}
                </div>
              </Tooltip>
            </CircleMarker>
          )
        })}
        {devices.map((d) => (
          <Fragment key={`dev-${d.id}`}>
            {hover === d.id && d.accuracyM > 0 && (
              <Circle center={[d.lat, d.lng]} radius={d.accuracyM} pathOptions={{ color: 'var(--series-2)', weight: 1, dashArray: '4 4', fillOpacity: 0.08 }} interactive={false} />
            )}
            <CircleMarker
              center={[d.lat, d.lng]}
              radius={5}
              pathOptions={{ color: dark ? '#0a0a0a' : '#ffffff', weight: 1.5, fillColor: d.online ? 'var(--good)' : 'var(--muted)', fillOpacity: 1 }}
              eventHandlers={{ click: () => onSelectDevice?.(d.id), mouseover: () => setHover(d.id), mouseout: () => setHover(null) }}
            >
              <Tooltip direction="top" offset={[0, -6]}>
                <div className="text-xs">
                  <div className="font-semibold">{d.hostname}</div>
                  <div>{d.online ? 'Online' : 'Offline'}{d.user ? ` · ${d.user}` : ''}</div>
                  <div className="text-muted">±{Math.round(d.accuracyM)} m{d.source ? ` · ${d.source}` : ''}{d.at ? ` · ${ago(d.at)}` : ''}</div>
                </div>
              </Tooltip>
            </CircleMarker>
          </Fragment>
        ))}
      </MapContainer>
      {offline && (
        <div className="pointer-events-none absolute bottom-2 right-3 z-500 rounded-md bg-panel/90 px-2 py-1 text-[10px] text-muted">
          Offline map · basemap unavailable
        </div>
      )}
    </div>
  )
}
