'use client'

import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'next/navigation'
import { AlertTriangle, Database } from 'lucide-react'

import { QueryConsole } from '@/components/databases/query-console'
import { ProblemBanner } from '@/components/problem-banner'
import { StatusDot } from '@/components/status-badge'
import { EmptyState, PageHeader, Panel } from '@/components/ui/panel'
import { client } from '@/lib/api/client'
import type { components } from '@/lib/api/schema'
import { apiState } from '@/lib/api/units'
import { cn } from '@/lib/utils'

type Db = components['schemas']['DatabaseSummaryDto']

export function QueryView() {
  const params = useSearchParams()
  const [rows, setRows] = useState<Db[]>([])
  const [error, setError] = useState<unknown>(null)
  const [selectedId, setSelectedId] = useState<string | undefined>(params.get('db') ?? undefined)

  useEffect(() => {
    client
      .GET('/api/v1/databases')
      .then((r) => {
        // The control-plane store is included. It was excluded on the grounds
        // that it holds every credential, session and audit row on the host —
        // true, and not a reason: an Airside login is a root login, and the
        // documented way out of a lockout is a psql shell on the same database.
        // Withholding it here protected nothing and removed the tool most likely
        // to answer the question someone came to this screen with.
        const all = r.data?.items ?? []

        setRows(all)

        // Defaults to a database of the operator's own when there is one, so the
        // console does not open pointed at Airside's internals.
        setSelectedId((id) => id ?? (all.find((d) => !d.isSystem) ?? all[0])?.id)
      })
      .catch(setError)
  }, [])

  const selected = useMemo(() => rows.find((d) => d.id === selectedId), [rows, selectedId])

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title="Query"
        description="Read and write the contents of a database. Separate from starting or stopping it."
      />
      {error != null && <ProblemBanner error={error} />}
      {rows.length === 0 || !selected ? (
        <EmptyState
          icon={Database}
          title="No database to query"
          description="Provision a database first. Query needs database.query."
        />
      ) : (
        <div className="grid grid-cols-1 gap-5 lg:grid-cols-[240px_1fr]">
          <Panel title="Databases" bodyClassName="p-0">
            <ul>
              {rows.map((d) => (
                <li key={d.id}>
                  <button
                    type="button"
                    onClick={() => setSelectedId(d.id)}
                    className={cn(
                      'flex w-full items-center gap-2 border-l-2 px-3 py-2.5 text-left',
                      d.id === selected.id
                        ? 'border-primary bg-primary/10'
                        : 'border-transparent text-muted-foreground hover:bg-secondary/60',
                    )}
                  >
                    <span className="min-w-0 flex-1 truncate font-mono text-xs">{d.slug}</span>
                    {d.isSystem && (
                      <span className="shrink-0 rounded bg-secondary px-1 py-0.5 font-mono text-[9px] uppercase tracking-wide text-muted-foreground">
                        core
                      </span>
                    )}
                    <StatusDot state={apiState(d.state)} />
                  </button>
                </li>
              ))}
            </ul>
          </Panel>
          <div className="flex min-w-0 flex-col gap-3">
            {selected.isSystem && (
              <p className="flex items-start gap-2 rounded-md border border-degraded/40 bg-degraded/10 px-3 py-2 text-xs text-degraded">
                <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
                <span>
                  This is Airside&apos;s own store — users, sessions, encrypted secrets and the audit
                  log. Reading it is audited. Writing to it can break the running control plane, and
                  needs <span className="font-mono">database.query_destructive</span> like anywhere
                  else.
                </span>
              </p>
            )}
            <QueryConsole key={selected.id} db={selected} />
          </div>
        </div>
      )}
    </div>
  )
}
