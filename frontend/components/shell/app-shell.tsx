'use client'

import { useEffect, useState } from 'react'
import { Menu, Plug, X } from 'lucide-react'

import { SidebarContent } from './sidebar'
import { client } from '@/lib/api/client'
import { useSession } from '@/lib/session'
import { cn } from '@/lib/utils'

export function AppShell({ children }: { children: React.ReactNode }) {
  const [open, setOpen] = useState(false)
  const { loading, user } = useSession()

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false)
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open])

  if (loading || !user) {
    return <div className="grid min-h-dvh place-items-center text-sm text-muted-foreground">Loading session…</div>
  }

  return (
    <div className="flex min-h-dvh bg-background">
      <aside className="hidden w-60 shrink-0 border-r border-border lg:block">
        <div className="sticky top-0 h-dvh">
          <SidebarContent />
        </div>
      </aside>

      {open && (
        <div className="fixed inset-0 z-50 lg:hidden">
          <button
            className="absolute inset-0 bg-background/80 backdrop-blur-sm"
            aria-label="Close navigation"
            onClick={() => setOpen(false)}
          />
          <div className="absolute left-0 top-0 h-full w-64 border-r border-border bg-background shadow-xl">
            <button
              className="absolute right-2 top-3 rounded-md p-1 text-muted-foreground hover:bg-secondary hover:text-foreground"
              aria-label="Close navigation"
              onClick={() => setOpen(false)}
            >
              <X className="size-5" />
            </button>
            <SidebarContent onNavigate={() => setOpen(false)} />
          </div>
        </div>
      )}

      <div className="flex min-w-0 flex-1 flex-col">
        <Topbar onMenu={() => setOpen(true)} />
        <main className="flex-1 px-4 py-5 sm:px-6 lg:px-8">{children}</main>
      </div>
    </div>
  )
}

function Topbar({ onMenu }: { onMenu: () => void }) {
  const { user, logout } = useSession()
  const [runtime, setRuntime] = useState<boolean | null>(null)
  const [hostName, setHostName] = useState<string>('this host')

  useEffect(() => {
    client
      .GET('/api/v1/system/info')
      .then((r) => {
        if (r.data) {
          setRuntime(r.data.runtimeAvailable)
          setHostName(r.data.instanceName)
        }
      })
      .catch(() => setRuntime(null))
  }, [])

  const initials = (user?.displayName ?? user?.email ?? '?')
    .split(' ')
    .map((p) => p[0])
    .slice(0, 2)
    .join('')
    .toUpperCase()

  return (
    <header className="sticky top-0 z-30 flex h-14 items-center gap-3 border-b border-border bg-background/90 px-4 backdrop-blur sm:px-6 lg:px-8">
      <button
        className="rounded-md p-1.5 text-muted-foreground hover:bg-secondary hover:text-foreground lg:hidden"
        aria-label="Open navigation"
        onClick={onMenu}
      >
        <Menu className="size-5" />
      </button>

      <div className="flex items-center gap-2">
        <span className="hidden font-mono text-xs text-muted-foreground sm:inline">host</span>
        <span className="font-mono text-sm font-medium text-foreground">{hostName}</span>
      </div>

      <div className="ml-auto flex items-center gap-3">
        {runtime != null && <DockerState connected={runtime} />}
        <div className="hidden h-5 w-px bg-border sm:block" />
        <button type="button" onClick={() => void logout()} className="flex items-center gap-2 text-left">
          <span className="grid size-7 place-items-center rounded-full bg-primary/15 font-mono text-xs font-semibold text-primary">
            {initials}
          </span>
          <span className="hidden text-sm text-foreground sm:inline">{user?.displayName}</span>
        </button>
      </div>
    </header>
  )
}

function DockerState({ connected }: { connected: boolean }) {
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1.5 rounded-md border px-2 py-1 font-mono text-xs',
        connected
          ? 'border-running/40 bg-running-soft text-running'
          : 'border-failed/40 bg-failed-soft text-failed',
      )}
      role="status"
    >
      <Plug className="size-3" aria-hidden />
      docker {connected ? 'connected' : 'unreachable'}
    </span>
  )
}
