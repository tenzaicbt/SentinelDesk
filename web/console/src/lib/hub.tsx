import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
import { tokenStore } from './api'

interface HubCtx {
  connection: HubConnection | null
  connected: boolean
}

const Ctx = createContext<HubCtx>({ connection: null, connected: false })

/** One SignalR connection per console session; pages subscribe to events / groups through hooks. */
export function HubProvider({ children }: { children: ReactNode }) {
  const [connection, setConnection] = useState<HubConnection | null>(null)
  const [connected, setConnected] = useState(false)

  useEffect(() => {
    const conn = new HubConnectionBuilder()
      .withUrl('/hubs/console', { accessTokenFactory: () => tokenStore.get() ?? '' })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 20000, 30000])
      .configureLogging(LogLevel.Warning)
      .build()
    conn.onreconnecting(() => setConnected(false))
    conn.onreconnected(() => setConnected(true))
    conn.onclose(() => setConnected(false))
    let disposed = false
    const start = async () => {
      try {
        await conn.start()
        if (!disposed) setConnected(true)
      } catch {
        if (!disposed) setTimeout(start, 5000)
      }
    }
    start()
    setConnection(conn)
    return () => { disposed = true; conn.stop() }
  }, [])

  return <Ctx.Provider value={{ connection, connected }}>{children}</Ctx.Provider>
}

export const useHub = () => useContext(Ctx)

/** Subscribe to a hub event for the lifetime of the component. */
export function useHubEvent<T>(event: string, handler: (data: T) => void) {
  const { connection } = useHub()
  const ref = useRef(handler)
  ref.current = handler
  useEffect(() => {
    if (!connection) return
    // must return void — SignalR treats a returned value as a client result
    const fn = (d: T) => { ref.current(d) }
    connection.on(event, fn)
    return () => connection.off(event, fn)
  }, [connection, event])
}

/** Join a server-side group (WatchDevice / WatchLive) while mounted, re-joining after reconnects. */
export function useHubGroup(join: string, leave: string, arg?: string) {
  const { connection, connected } = useHub()
  useEffect(() => {
    if (!connection || !connected || connection.state !== HubConnectionState.Connected) return
    const args = arg ? [arg] : []
    connection.invoke(join, ...args).catch(() => {})
    return () => { if (connection.state === HubConnectionState.Connected) connection.invoke(leave, ...args).catch(() => {}) }
  }, [connection, connected, join, leave, arg])
}

/** Sub-second sample relayed from the agent while this device is being watched (never stored). */
export interface LiveSample {
  deviceId: string
  seq: number
  agentTimeMs: number
  serverTimeMs: number
  intervalMs: number
  cpu: number
  ram: number
  netInBps: number
  netOutBps: number
  currentApp?: string
  currentTitle?: string
  idle: boolean
  locked: boolean
}

export interface Presence {
  deviceId: string
  hostname: string
  orgUnitId?: string
  lastSeen: string
  user?: string
  ip?: string
  cpu: number
  ram: number
  disk: number
  netInBps: number
  netOutBps: number
  todayIn: number
  todayOut: number
  currentApp?: string
  currentTitle?: string
  idle: boolean
  locked: boolean
  uptimeSec: number
  agentVersion: string
  trayRunning: boolean
  queueDepth: number
  connectionType?: string
  realtime: boolean
}
