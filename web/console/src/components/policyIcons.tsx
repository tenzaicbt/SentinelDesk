import type { ReactNode } from 'react'
import { AppWindow, Globe, Usb } from 'lucide-react'
import type { PolicyKind } from '../lib/policy'

export const KIND_ICON: Record<PolicyKind, ReactNode> = {
  websites: <Globe className="size-5" />, apps: <AppWindow className="size-5" />, usb: <Usb className="size-5" />,
}
