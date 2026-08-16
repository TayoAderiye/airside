'use client'

import { useEffect, useState } from 'react'
import { useSearchParams } from 'next/navigation'
import { Database, Loader2, Rocket } from 'lucide-react'

import { PageHeader, Panel } from '@/components/ui/panel'
import { ProblemBanner } from '@/components/problem-banner'
import { StatusDot } from '@/components/status-badge'
import { LogStream } from '@/components/logs/log-stream'
import { client } from '@/lib/api/client'
import { apiState } from '@/lib/api/units'
import { cn } from '@/lib/utils'

type Source = {
  id: string
  name: string
  kind: 'database' | 'application'
  state: string

  /**
   * Airside's own containers.
   *
   * Their ids are synthesised and match no row, so nothing that reads a table
   * can find them — no detail page, no lifecycle action. The log stream
   * resolves them through a name allowlist instead, so they are readable here
   * while staying unstoppable from the dashboard they are serving.
   */
  isSystem: boolean
}

export function MonitoringView() {
  const params = useSearchParams()
  const requested = params.get('workload')
  const [sources, setSources] = useState<Source[] | null>(null)
  const [selected, setSelected] = useState<Source | null>(null)
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    let cancelled = false

    Promise.all([client.GET('/api/v1/applications'), client.GET('/api/v1/databases')])
      .then(([appRes, dbRes]) => {
        if (cancelled) return

        const list: Source[] = [
          ...(appRes.data?.items ?? []).map((a) => ({
            id: a.id,
            name: a.displayName || a.slug,
            kind: 'application' as const,
            state: a.state,
            isSystem: a.isSystem,
          })),
          ...(dbRes.data?.items ?? []).map((d) => ({
            id: d.id,
            name: d.displayName || d.slug,
            kind: 'database' as const,
            state: d.state,
            isSystem: d.isSystem,
          })),
        ]

        setSources(list)

        // Honours ?workload= so the "View log" links on the list screens land
        // on the right row rather than on whatever happens to be first.
        setSelected(list.find((s) => s.id === requested) ?? list[0] ?? null)
      })
      .catch((err) => {
        if (cancelled) return
        setError(err)
        setSources([])
      })

    return () => {
      cancelled = true
    }
  }, [requested])

  if (sources === null) {
    return (
      <p className="flex items-center gap-2 text-sm text-muted-foreground">
        <Loader2 className="size-4 animate-spin text-transitional" />
        Loading workloads…
      </p>
    )
  }

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title="Monitoring"
        description="Live container output from every workload on this host, including Airside's own."
      />

      {error != null && <ProblemBanner error={error} />}

      <div className="grid grid-cols-1 gap-5 lg:grid-cols-[240px_1fr]">
        <Panel title="Workloads" bodyClassName="p-0">
          {sources.length === 0 ? (
            <p className="p-4 text-sm text-muted-foreground">Nothing running yet.</p>
          ) : (
            <ul className="flex flex-col">
              {sources.map((s) => {
                const active = s.id === selected?.id
                const Icon = s.kind === 'application' ? Rocket : Database

                return (
                  <li key={s.id}>
                    <button
                      type="button"
                      onClick={() => setSelected(s)}
                      aria-pressed={active}
                      className={cn(
                        'flex w-full items-center gap-2 border-l-2 px-3 py-2 text-left text-sm transition-colors',
                        active
                          ? 'border-primary bg-accent/10 text-foreground'
                          : 'border-transparent text-muted-foreground hover:text-foreground',
                      )}
                    >
                      <Icon className="size-3.5 shrink-0" />
                      <span className="min-w-0 flex-1 truncate">{s.name}</span>
                      <StatusDot state={apiState(s.state)} />
                    </button>
                  </li>
                )
              })}
            </ul>
          )}
        </Panel>

        <Panel
          title={selected ? `Live log — ${selected.name}` : 'Live log'}
          description={
            selected?.isSystem
              ? 'Part of Airside itself. Readable here; startable and stoppable only on the host.'
              : undefined
          }
          bodyClassName="p-0"
        >
          {!selected ? (
            <p className="p-4 text-sm text-muted-foreground">Select a workload.</p>
          ) : (
            // Every workload, Airside's own included. This used to refuse for
            // system containers and for all applications, which between them
            // was everything on a host with no database — a monitoring screen
            // that monitored nothing and told you to use ssh.
            <LogStream kind={selected.kind} id={selected.id} height="30rem" />
          )}
        </Panel>

      </div>
    </div>
  )
}
