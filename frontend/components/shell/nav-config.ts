import {
  Activity,
  Bell,
  Database,
  FileArchive,
  Globe,
  HardDrive,
  LayoutDashboard,
  Rocket,
  ScrollText,
  Server,
  Settings,
  ShieldCheck,
  SquareTerminal,
  Upload,
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

/**
 * Screens the API actually supports. No host switcher, no orgs, no billing.
 *
 * Every entry here is backed by real endpoints. Two screens were removed rather
 * than listed — a networks view, for which there is no API at all, and a secrets
 * view, for a concept Airside does not have: secrets are an application's
 * environment variables and a database's credentials, and both are reachable
 * from the workload that owns them.
 */
export const navGroups: NavGroup[] = [
  {
    items: [
      { label: 'Overview', href: '/dashboard', icon: LayoutDashboard },
      { label: 'Applications', href: '/applications', icon: Rocket },
      { label: 'Databases', href: '/databases', icon: Database },
      { label: 'Query', href: '/query', icon: SquareTerminal },
      { label: 'Domains', href: '/domains', icon: Globe },
      { label: 'Notifications', href: '/notifications', icon: Bell },
    ],
  },
  {
    heading: 'Operations',
    items: [
      { label: 'Deployments', href: '/deployments', icon: Upload },
      { label: 'Monitoring', href: '/monitoring', icon: Activity },
      { label: 'Backups', href: '/backups', icon: FileArchive },
    ],
  },
  {
    heading: 'Host',
    items: [
      { label: 'Server', href: '/infrastructure/servers', icon: Server },
      { label: 'Storage', href: '/infrastructure/storage', icon: HardDrive },
    ],
  },
  {
    heading: 'Administration',
    items: [
      { label: 'Users & access', href: '/access', icon: ShieldCheck },
      { label: 'Audit log', href: '/audit', icon: ScrollText },
      { label: 'Settings', href: '/settings', icon: Settings },
    ],
  },
]
