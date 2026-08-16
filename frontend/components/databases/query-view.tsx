'use client'

import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'next/navigation'
import { Database } from 'lucide-react'

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
  const [hasSystemStore, setHasSystemStore] = useState(false)

  useEffect(() => {
    client
      .GET('/api/v1/databases')
      .then((r) => {
        // The control-plane store is excluded, and not only because a query
        // against it would 404 — its id is synthesised and belongs to no row.
        // It holds every credential, session and audit entry on the host, and a
        // console pointed at it is not a feature Airside should offer.
        const all = r.data?.items ?? []
        const items = all.filter((d) => !d.isSystem)

        setHasSystemStore(all.length > items.length)
        setRows(items)
        setSelectedId((id) => id ?? items[0]?.id)
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
        // Saying "provision a database first" while the Databases screen shows
        // one running reads as a broken page. The control-plane store is there,
        // it is deliberately not queryable, and that is worth one sentence.
        <EmptyState
          icon={Database}
          title="No database to query"
          description={
            hasSystemStore
              ? "Airside's own store is running, but it is not queryable here — it holds every credential, session and audit row on this host. Provision a database of your own to use this console."
              : 'Provision a database first. Query needs database.query.'
          }
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
                    <StatusDot state={apiState(d.state)} />
                  </button>
                </li>
              ))}
            </ul>
          </Panel>
          <QueryConsole key={selected.id} db={selected} />
        </div>
      )}
    </div>
  )
}
