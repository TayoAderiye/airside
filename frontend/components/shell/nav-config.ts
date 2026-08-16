import {
  Bell,
  Database,
  Globe,
  LayoutDashboard,
  Rocket,
  Settings,
  SquareTerminal,
  type LucideIcon,
} from 'lucide-react'

export interface NavItem {
  label: string
  href: string
  icon: LucideIcon
}

export interface NavGroup {
  heading?: string
  items: NavItem[]
}

/** Screens the API actually supports. No host switcher, no orgs, no billing. */
export const navGroups: NavGroup[] = [
  {
    items: [
      { label: 'Overview', href: '/dashboard', icon: LayoutDashboard },
      { label: 'Applications', href: '/applications', icon: Rocket },
      { label: 'Databases', href: '/databases', icon: Database },
      { label: 'Query', href: '/query', icon: SquareTerminal },
      { label: 'Domains', href: '/domains', icon: Globe },
      { label: 'Notifications', href: '/notifications', icon: Bell },
      { label: 'Settings', href: '/settings', icon: Settings },
    ],
  },
]
