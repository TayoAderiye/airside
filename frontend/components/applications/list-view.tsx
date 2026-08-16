'use client'

import { useEffect, useState } from 'react'
import Link from 'next/link'
import { Plus } from 'lucide-react'

import { ProblemBanner } from '@/components/problem-banner'
import { StatusBadge } from '@/components/status-badge'
import { buttonVariants } from '@/components/ui/button'
import { EmptyState, PageHeader } from '@/components/ui/panel'
import { client } from '@/lib/api/client'
import type { components } from '@/lib/api/schema'
import { apiState, bytesToGiB, nanosToCores } from '@/lib/api/units'
import { cn } from '@/lib/utils'
import { Rocket } from 'lucide-react'

type App = components['schemas']['ApplicationSummaryDto']

export function ApplicationsList() {
  const [apps, setApps] = useState<App[]>([])
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    client
      .GET('/api/v1/applications')
      .then((r) => setApps(r.data?.items ?? []))
      .catch(setError)
  }, [])

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title="Applications"
        description="Workloads on this host."
        actions={
          <Link href="/applications/new" className={cn(buttonVariants(), 'gap-1.5')}>
            <Plus className="size-4" /> Deploy app
          </Link>
        }
      />
      {error != null && <ProblemBanner error={error} />}
      {apps.length === 0 && !error ? (
        <EmptyState
          icon={Rocket}
          title="No applications"
          description="Create an application, then deploy an image or a git repository."
          action={
            <Link href="/applications/new" className={cn(buttonVariants(), 'gap-1.5')}>
              <Plus className="size-4" /> Deploy app
            </Link>
          }
        />
      ) : (
        <ul className="flex flex-col gap-3">
          {apps.map((app) => {
            // Airside's own containers. No detail page and no lifecycle Airside
            // owns — nothing here offers to stop the API serving this page — but
            // the log is readable, and offering nothing at all left four rows on
            // this screen that an operator could look at and not act on.
            if (app.isSystem) {
              return (
                <li key={app.id}>
                  <div className="flex flex-col gap-3 rounded-lg border border-dashed border-border bg-card/50 p-4 lg:flex-row lg:items-center">
                    <div className="min-w-0 flex-1">
                      <p className="flex items-center gap-2 font-display text-sm font-semibold">
                        {app.displayName || app.slug}
                        <span className="rounded bg-secondary px-1.5 py-0.5 font-mono text-[10px] font-normal uppercase tracking-wide text-muted-foreground">
                          control plane
                        </span>
                      </p>
                      <p className="font-mono text-xs text-muted-foreground">
                        {app.slug} · port {app.containerPort} · managed by the installer, not by Airside
                      </p>
                    </div>
                    <Link
                      href={`/monitoring?workload=${app.id}`}
                      className="font-mono text-xs text-primary hover:underline"
                    >
                      View log
                    </Link>
                    <StatusBadge state={apiState(app.state)} />
                  </div>
                </li>
              )
            }

            return (
              <li key={app.id}>
                <Link
                  href={`/applications/${app.id}`}
                  className="flex flex-col gap-3 rounded-lg border border-border bg-card p-4 hover:border-ring/60 lg:flex-row lg:items-center"
                >
                  <div className="min-w-0 flex-1">
                    <p className="font-display text-sm font-semibold">{app.displayName || app.slug}</p>
                    <p className="font-mono text-xs text-muted-foreground">
                      {app.sourceKind} · port {app.containerPort} · {nanosToCores(app.cpuNanos).toFixed(2)} cores ·{' '}
                      {bytesToGiB(app.memoryBytes).toFixed(1)} GiB
                    </p>
                  </div>
                  <StatusBadge state={apiState(app.state)} />
                </Link>
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}
