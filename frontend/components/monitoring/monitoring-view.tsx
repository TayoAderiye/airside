'use client'

import { useEffect, useState } from 'react'
import Link from 'next/link'
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
}

export function MonitoringView() {
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
          })),
          ...(dbRes.data?.items ?? []).map((d) => ({
            id: d.id,
            name: d.displayName || d.slug,
            kind: 'database' as const,
            state: d.state,
          })),
        ]

        setSources(list)

        // Defaults to something that can actually stream, so the panel is not
        // empty on arrival for a host that has both kinds.
        setSelected(list.find((s) => s.kind === 'database') ?? list[0] ?? null)
      })
      .catch((err) => {
        if (cancelled) return
        setError(err)
        setSources([])
      })

    return () => {
      cancelled = true
    }
  }, [])

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
        description="Live container output. Databases stream; application logs are per deployment."
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

        <Panel title={selected ? `Live log — ${selected.name}` : 'Live log'} bodyClassName="p-0">
          {!selected ? (
            <p className="p-4 text-sm text-muted-foreground">Select a workload.</p>
          ) : selected.kind === 'database' ? (
            <LogStream databaseId={selected.id} height="30rem" />
          ) : (
            // Said plainly rather than shown as an empty stream. The API has a
            // log stream for databases and none for applications; an
            // application's output is captured per deployment instead.
            <div className="p-4">
              <p className="text-sm text-foreground">Applications have no live log stream yet.</p>
              <p className="mt-1 text-sm text-muted-foreground">
                Their output is recorded against each deployment.
              </p>
              <Link
                href={`/applications/${selected.id}`}
                className="mt-3 inline-block font-mono text-xs text-primary hover:underline"
              >
                Open {selected.name}
              </Link>
            </div>
          )}
        </Panel>
      </div>
    </div>
  )
}
