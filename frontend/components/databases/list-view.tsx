'use client'

import { useEffect, useState } from 'react'
import Link from 'next/link'
import { Database, Plus, SquareTerminal } from 'lucide-react'

import { ProblemBanner } from '@/components/problem-banner'
import { StatusBadge } from '@/components/status-badge'
import { buttonVariants } from '@/components/ui/button'
import { EmptyState, PageHeader } from '@/components/ui/panel'
import { client } from '@/lib/api/client'
import type { components } from '@/lib/api/schema'
import { apiState } from '@/lib/api/units'
import { cn } from '@/lib/utils'

type Db = components['schemas']['DatabaseSummaryDto']

export function DatabasesList() {
  const [rows, setRows] = useState<Db[]>([])
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    client
      .GET('/api/v1/databases')
      .then((r) => setRows(r.data?.items ?? []))
      .catch(setError)
  }, [])

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title="Databases"
        description="Engines running on this host."
        actions={
          <Link href="/databases/new" className={cn(buttonVariants(), 'gap-1.5')}>
            <Plus className="size-4" /> New database
          </Link>
        }
      />
      {error != null && <ProblemBanner error={error} />}
      {rows.length === 0 && !error ? (
        <EmptyState
          icon={Database}
          title="No databases on this host"
          description="Provision an engine. Capacity is reserved when the job is accepted."
          action={
            <Link href="/databases/new" className={cn(buttonVariants(), 'gap-1.5')}>
              <Plus className="size-4" /> New database
            </Link>
          }
        />
      ) : (
        <ul className="flex flex-col gap-3">
          {rows.map((db) => {
            // The control-plane store. No detail page and no query console: this
            // is the database holding every credential, session and audit row on
            // the host, and a console pointed at it is not a feature. Its log is
            // fair game, and used to be the one thing on this row that looked
            // clickable and was not.
            if (db.isSystem) {
              return (
                <li
                  key={db.id}
                  className="flex flex-col gap-3 rounded-lg border border-dashed border-border bg-card/50 p-4 lg:flex-row lg:items-center"
                >
                  <div className="min-w-0 flex-1">
                    <p className="flex items-center gap-2 font-medium">
                      {db.displayName || db.slug}
                      <span className="rounded bg-secondary px-1.5 py-0.5 font-mono text-[10px] font-normal uppercase tracking-wide text-muted-foreground">
                        control plane
                      </span>
                    </p>
                    <p className="font-mono text-xs text-muted-foreground">
                      {db.engine} {db.version} · Airside&apos;s own store · not queryable
                    </p>
                  </div>
                  <Link
                    href={`/monitoring?workload=${db.id}`}
                    className="font-mono text-xs text-primary hover:underline"
                  >
                    View log
                  </Link>
                  <StatusBadge state={apiState(db.state)} />
                </li>
              )
            }

            return (
              <li
                key={db.id}
                className="flex flex-col gap-3 rounded-lg border border-border bg-card p-4 lg:flex-row lg:items-center"
              >
                <Link href={`/databases/${db.id}`} className="min-w-0 flex-1">
                  <p className="font-medium">{db.displayName || db.slug}</p>
                  <p className="font-mono text-xs text-muted-foreground">
                    {db.engine} {db.version}
                  </p>
                </Link>
                <StatusBadge state={apiState(db.state)} />
                <Link
                  href={`/query?db=${db.id}`}
                  className={cn(buttonVariants({ variant: 'outline', size: 'sm' }), 'gap-1.5')}
                >
                  <SquareTerminal className="size-3.5" /> Query
                </Link>
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}
