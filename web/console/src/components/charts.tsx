import { useId, type ReactNode } from 'react'
import { Area, AreaChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { bytes } from '../lib/format'

/**
 * Series colors come from validated palette tokens (index.css; light and dark steps selected and checked separately
 * with the dataviz validator). Download = brand orange, upload = indigo.
 */
export const S = {
  down: 'var(--series-1)',
  up: 'var(--series-2)',
  active: 'var(--series-1)',
  idle: 'var(--axis)',
  cpu: 'var(--series-1)',
  ram: 'var(--series-2)',
  disk: 'var(--series-3)',
}

export function Legend({ items }: { items: { label: string; color: string }[] }) {
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-fg-2">
      {items.map((i) => (
        <span key={i.label} className="inline-flex items-center gap-1.5">
          <span className="inline-block size-2.5 rounded-[3px]" style={{ background: i.color }} />
          {i.label}
        </span>
      ))}
    </div>
  )
}

interface TipItem { name?: unknown; value?: unknown; color?: string; dataKey?: unknown }
// eslint-disable-next-line @typescript-eslint/no-explicit-any
type TipProps = { active?: boolean; payload?: readonly any[]; label?: unknown }

/** shadcn-style tooltip: label, one row per series with a color indicator, optional total. */
export function ChartTooltip({ active, payload, label, formatter, labelFormatter, total }: TipProps & {
  formatter?: (v: number, name: string) => ReactNode
  labelFormatter?: (l: string | number) => ReactNode
  total?: boolean
}) {
  if (!active || !payload?.length) return null
  const items = payload as TipItem[]
  const sum = items.reduce((s, p) => s + (Number(p.value) || 0), 0)
  const fmt = (v: number, n: string) => (formatter ? formatter(v, n) : String(v))
  return (
    <div className="grid min-w-40 gap-1.5 rounded-lg border border-line bg-panel px-2.5 py-1.5 text-xs shadow-xl">
      <div className="font-medium text-fg">{labelFormatter ? labelFormatter(label as string) : String(label ?? '')}</div>
      <div className="grid gap-1">
        {items.map((p) => (
          <div key={String(p.dataKey)} className="flex items-center gap-2">
            <span className="h-2.5 w-1 shrink-0 rounded-[2px]" style={{ background: p.color }} />
            <span className="flex-1 text-muted">{String(p.name)}</span>
            <span className="font-mono font-medium tabular text-fg">{fmt(Number(p.value), String(p.name))}</span>
          </div>
        ))}
        {total && items.length > 1 && (
          <div className="mt-0.5 flex items-center gap-2 border-t border-line pt-1">
            <span className="w-1" />
            <span className="flex-1 font-medium text-fg">Total</span>
            <span className="font-mono font-medium tabular text-fg">{fmt(sum, 'Total')}</span>
          </div>
        )}
      </div>
    </div>
  )
}

const xAxis = { tickLine: false, axisLine: false, tickMargin: 8, minTickGap: 32, fontSize: 11 } as const

interface Series { key: string; name: string; color: string }

/**
 * The one area-chart look used everywhere: natural curves, vertical gradient fills, horizontal grid only,
 * no y-axis (values live in the crosshair tooltip), 2px strokes and a surface-ringed active dot.
 */
function AreaTrend({ data, xKey, series, height, stacked, yMax, yFormat, xFormat, tipFormat, total }: {
  data: Record<string, unknown>[]; xKey: string; series: Series[]; height: number; stacked?: boolean
  yMax?: number; yFormat?: (v: number) => string; xFormat?: (v: string) => string
  tipFormat: (v: number) => ReactNode; total?: boolean
}) {
  const uid = useId().replace(/:/g, '')
  const few = data.length < 3
  return (
    <ResponsiveContainer width="100%" height={height}>
      <AreaChart data={data} margin={{ top: 10, right: 6, left: 6, bottom: 0 }}>
        <defs>
          {series.map((s) => (
            <linearGradient key={s.key} id={`${uid}-${s.key}`} x1="0" y1="0" x2="0" y2="1">
              <stop offset="5%" stopColor={s.color} stopOpacity={stacked ? 0.75 : 0.45} />
              <stop offset="95%" stopColor={s.color} stopOpacity={stacked ? 0.08 : 0.02} />
            </linearGradient>
          ))}
        </defs>
        <CartesianGrid vertical={false} strokeDasharray="0" />
        <XAxis dataKey={xKey} {...xAxis} tickFormatter={xFormat} />
        <YAxis hide domain={[0, yMax ?? 'auto']} tickFormatter={yFormat} />
        <Tooltip
          cursor={{ stroke: 'var(--muted)', strokeWidth: 1, strokeDasharray: '3 3' }}
          content={(p) => <ChartTooltip active={p.active} payload={p.payload} label={p.label} total={total}
            formatter={(v) => tipFormat(v)} labelFormatter={(l) => (xFormat ? xFormat(String(l)) : l)} />}
        />
        {/* stacked: draw bottom series first so the top band sits above it */}
        {series.map((s) => (
          <Area key={s.key} type="natural" dataKey={s.key} name={s.name} stackId={stacked ? 'a' : undefined}
            stroke={s.color} strokeWidth={2} fill={`url(#${uid}-${s.key})`} fillOpacity={1}
            dot={few ? { r: 3, fill: s.color, stroke: 'var(--panel)', strokeWidth: 2 } : false}
            activeDot={{ r: 4.5, fill: s.color, stroke: 'var(--panel)', strokeWidth: 2 }}
            isAnimationActive animationDuration={600} />
        ))}
      </AreaChart>
    </ResponsiveContainer>
  )
}

/** Download/upload over time - stacked gradient areas (total height = all traffic). */
export function UsageBars({ data, xKey, xFormat, height = 240 }: {
  data: { bytesIn: number; bytesOut: number; [k: string]: unknown }[]; xKey: string; xFormat?: (v: string) => string; height?: number
}) {
  return (
    <AreaTrend data={data} xKey={xKey} xFormat={xFormat} height={height} stacked total
      series={[{ key: 'bytesIn', name: 'Download', color: S.down }, { key: 'bytesOut', name: 'Upload', color: S.up }]}
      tipFormat={(v) => bytes(v)} />
  )
}

/** One or a few series on the same unit (e.g. CPU %, live throughput) - overlaid, not stacked. */
export function Lines({ data, xKey, series, height = 220, yMax, yFormat, xFormat }: {
  data: Record<string, unknown>[]; xKey: string; series: Series[]
  height?: number; yMax?: number; yFormat?: (v: number) => string; xFormat?: (v: string) => string
}) {
  return (
    <AreaTrend data={data} xKey={xKey} series={series} height={height} yMax={yMax} yFormat={yFormat} xFormat={xFormat}
      tipFormat={(v) => (yFormat ? yFormat(v) : String(v))} />
  )
}

/** Ranked horizontal bars rendered in HTML (crisper labels than SVG, scales to any count). */
export function RankBars({ rows, format, color = 'var(--series-1)', onClick }: {
  rows: { label: ReactNode; value: number; sub?: ReactNode; key: string }[]; format: (v: number) => string; color?: string; onClick?: (key: string) => void
}) {
  const max = Math.max(1, ...rows.map((r) => r.value))
  return (
    <ul className="space-y-2.5">
      {rows.map((r) => (
        <li key={r.key} className={onClick ? 'group cursor-pointer' : 'group'} onClick={() => onClick?.(r.key)} title={`${format(r.value)}`}>
          <div className="mb-1 flex items-baseline justify-between gap-3 text-[13px]">
            <span className="min-w-0 truncate text-fg group-hover:underline decoration-line-strong underline-offset-2">{r.label}</span>
            <span className="shrink-0 tabular text-fg-2">{format(r.value)}{r.sub && <span className="ml-1.5 text-muted">{r.sub}</span>}</span>
          </div>
          <div className="h-1.5 rounded-full bg-panel-2">
            <div className="h-full rounded-full transition-[width] duration-700" style={{ width: `${Math.max(2, (r.value / max) * 100)}%`, background: color }} />
          </div>
        </li>
      ))}
    </ul>
  )
}

/** Active vs idle minutes by hour - stacked gradient areas. */
export function ActivityBars({ data, height = 200 }: { data: { hour: number; activeSec: number; idleSec: number }[]; height?: number }) {
  const d = data.map((x) => ({ label: `${String(x.hour).padStart(2, '0')}:00`, active: Math.round(x.activeSec / 60), idle: Math.round(x.idleSec / 60) }))
  return (
    <AreaTrend data={d} xKey="label" height={height} stacked total
      series={[{ key: 'active', name: 'Active', color: S.active }, { key: 'idle', name: 'Idle', color: S.idle }]}
      tipFormat={(v) => `${v} min`} />
  )
}

/** Smooth mini trend with a soft gradient (live tiles). */
export function Sparkline({ values, color = 'var(--series-1)', height = 28 }: { values: number[]; color?: string; height?: number }) {
  const uid = useId().replace(/:/g, '')
  if (values.length < 2) return <div style={{ height }} />
  const max = Math.max(1, ...values)
  const w = 100
  const pts = values.map((v, i) => [(i / (values.length - 1)) * w, height - 2 - (v / max) * (height - 4)] as const)
  // Catmull-Rom -> cubic Bezier for a natural curve
  let path = `M${pts[0][0]},${pts[0][1]}`
  for (let i = 0; i < pts.length - 1; i++) {
    const [x0, y0] = pts[Math.max(0, i - 1)], [x1, y1] = pts[i], [x2, y2] = pts[i + 1], [x3, y3] = pts[Math.min(pts.length - 1, i + 2)]
    path += ` C${x1 + (x2 - x0) / 6},${y1 + (y2 - y0) / 6} ${x2 - (x3 - x1) / 6},${y2 - (y3 - y1) / 6} ${x2},${y2}`
  }
  return (
    <svg viewBox={`0 0 ${w} ${height}`} preserveAspectRatio="none" className="w-full" style={{ height }} aria-hidden>
      <defs>
        <linearGradient id={`sp-${uid}`} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={color} stopOpacity={0.35} />
          <stop offset="100%" stopColor={color} stopOpacity={0} />
        </linearGradient>
      </defs>
      <path d={`${path} L${w},${height} L0,${height} Z`} fill={`url(#sp-${uid})`} />
      <path d={path} fill="none" stroke={color} strokeWidth="2" vectorEffect="non-scaling-stroke" strokeLinejoin="round" strokeLinecap="round" />
    </svg>
  )
}
