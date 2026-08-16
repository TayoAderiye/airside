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

  useEffect(() => {
    client
      .GET('/api/v1/databases')
      .then((r) => {
        const items = r.data?.items ?? []
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
