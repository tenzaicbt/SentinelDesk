/** Block policies: the three kinds of restriction the console manages. */
export interface BlockSettings { apps: string[]; urls: string[]; categories: string[]; notifyUser: boolean; message: string }

/** The policy document. Only `block` (websites / apps) and `collectors.usbBlock` (usb) matter for block policies. */
export interface Settings {
  collectors: { usbBlock: boolean; [key: string]: unknown }
  block?: BlockSettings
  [key: string]: unknown
}

export type PolicyKind = 'websites' | 'apps' | 'usb'

export interface PolicyRow {
  id: string; name: string; description?: string; version: number; kind: PolicyKind
  updatedAt: string; updatedBy?: string; settings: Settings
  assignments: { id: string; targetType: string; targetId: string; name?: string }[]
}

export const KIND_ORDER: PolicyKind[] = ['websites', 'apps', 'usb']

export const KINDS: Record<PolicyKind, { label: string; short: string; text: string; placeholder: string }> = {
  websites: { label: 'Block websites', short: 'Websites', text: 'Stop websites from opening, by address or by type (social media, games…).', placeholder: 'e.g. Block social media' },
  apps: { label: 'Block applications', short: 'Applications', text: 'Stop programs from running, such as games or file-sharing apps.', placeholder: 'e.g. Block games' },
  usb: { label: 'Block USB drives', short: 'USB drives', text: 'Stop pen drives and external hard disks from being used.', placeholder: 'e.g. No USB at cashier counters' },
}

export const DEFAULT_BLOCK: BlockSettings = { apps: [], urls: [], categories: [], notifyUser: true, message: "This application is blocked by your organisation's policy." }

/** Must match Domains.BlockableCategories on the server. */
export const BLOCK_CATEGORIES: { name: string; examples: string }[] = [
  { name: 'Social', examples: 'Facebook, Instagram, TikTok, X' },
  { name: 'Messaging', examples: 'WhatsApp, Telegram, Discord' },
  { name: 'Streaming', examples: 'YouTube, Netflix, Spotify' },
  { name: 'Gaming', examples: 'Steam, Epic Games, Roblox' },
  { name: 'Shopping', examples: 'Daraz, Amazon, ikman' },
  { name: 'News', examples: 'BBC, Daily Mirror, Ada Derana' },
  { name: 'AI Tools', examples: 'ChatGPT, Claude, Gemini' },
  { name: 'Cloud Storage', examples: 'Dropbox, WeTransfer, MEGA' },
]

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`

/** What a policy blocks, in a few words. */
export function summary(p: PolicyRow): string {
  const b = p.settings.block ?? DEFAULT_BLOCK
  switch (p.kind) {
    case 'websites': {
      const parts = [b.urls.length ? plural(b.urls.length, 'website', 'websites') : '', b.categories.join(', ')].filter(Boolean)
      return parts.length ? `Blocks ${parts.join(' and ')}` : 'No websites added yet'
    }
    case 'apps': return b.apps.length ? `Blocks ${plural(b.apps.length, 'application', 'applications')}` : 'No applications added yet'
    default: return 'Blocks USB drives'
  }
}
