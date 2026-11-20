export function bytes(n: number | null | undefined, digits = 1): string {
  if (n === null || n === undefined || isNaN(n)) return '—'
  const u = ['B', 'KB', 'MB', 'GB', 'TB']
  let i = 0
  let v = Math.abs(n)
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++ }
  return `${v.toFixed(i === 0 ? 0 : v < 10 ? digits : v < 100 ? Math.min(digits, 1) : 0)} ${u[i]}`
}

export const bps = (n: number | null | undefined) => (n === null || n === undefined ? '—' : `${bytes(n)}/s`)

export function duration(sec: number | null | undefined): string {
  if (!sec) return '0m'
  const h = Math.floor(sec / 3600)
  const m = Math.round((sec % 3600) / 60)
  if (h >= 24) return `${Math.floor(h / 24)}d ${h % 24}h`
  return h ? `${h}h ${m}m` : `${m}m`
}

export function ago(iso: string | null | undefined): string {
  if (!iso) return 'never'
  const s = (Date.now() - new Date(iso).getTime()) / 1000
  if (s < 45) return 'just now'
  if (s < 3600) return `${Math.round(s / 60)} min ago`
  if (s < 86400) return `${Math.round(s / 3600)} h ago`
  if (s < 86400 * 30) return `${Math.round(s / 86400)} d ago`
  return new Date(iso).toLocaleDateString()
}

export const dateTime = (iso: string | null | undefined) =>
  iso ? new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : '—'

export const time = (iso: string) => new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })

export const shortDay = (d: string) => new Date(d + 'T00:00:00').toLocaleDateString(undefined, { weekday: 'short', day: 'numeric' })

export const num = (n: number | null | undefined) => (n === null || n === undefined ? '—' : n.toLocaleString())

export const pct = (n: number | null | undefined) => (n === null || n === undefined ? '—' : `${Math.round(n)}%`)

export const appName = (exe: string) => {
  const map: Record<string, string> = {
    'chrome.exe': 'Google Chrome', 'msedge.exe': 'Microsoft Edge', 'firefox.exe': 'Firefox', 'OUTLOOK.EXE': 'Outlook',
    'EXCEL.EXE': 'Excel', 'WINWORD.EXE': 'Word', 'POWERPNT.EXE': 'PowerPoint', 'ms-teams.exe': 'Microsoft Teams',
    'Teams.exe': 'Microsoft Teams', 'saplogon.exe': 'SAP GUI', 'AcroRd32.exe': 'Adobe Acrobat', 'Acrobat.exe': 'Adobe Acrobat',
    'explorer.exe': 'File Explorer', 'WhatsApp.exe': 'WhatsApp', 'Code.exe': 'VS Code', 'AutoCAD.exe': 'AutoCAD',
    'acad.exe': 'AutoCAD', 'vlc.exe': 'VLC', 'Zoom.exe': 'Zoom', 'notepad.exe': 'Notepad', 'devenv.exe': 'Visual Studio',
    'WindowsTerminal.exe': 'Terminal', 'Spotify.exe': 'Spotify',
  }
  return map[exe] ?? exe.replace(/\.exe$/i, '')
}

export const today = () => new Date().toISOString().slice(0, 10)
export const daysAgo = (n: number) => new Date(Date.now() - n * 86400000).toISOString().slice(0, 10)
