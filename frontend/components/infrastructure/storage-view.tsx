'use client'

import { useEffect, useState } from 'react'
import Link from 'next/link'
import { HardDrive, Loader2, Unplug } from 'lucide-react'

import { ResourceMeter } from '@/components/resource-meter'
import { ProblemBanner } from '@/components/problem-banner'
import { EmptyState, Mono, PageHeader, Panel } from '@/components/ui/panel'
import { client } from '@/lib/api/client'
import { bytesToGiB } from '@/lib/api/units'
import type { components } from '@/lib/api/schema'
import { formatRelative } from '@/lib/status'

type Volume = components['schemas']['VolumeDto']

export function StorageView() {
  const [volumes, setVolumes] = useState<Volume[] | null>(null)

  /**
   * Which workloads are databases, so a volume can link to the right screen.
   *
   * VolumeDto names its workload by id and slug but not by kind, and the two
   * detail routes are different. What this replaced solved it with a hardcoded
   * table mapping five invented names to five invented ids.
   */
  const [databaseIds, setDatabaseIds] = useState<Set<string>>(new Set())
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    let cancelled = false

    Promise.all([client.GET('/api/v1/volumes'), client.GET('/api/v1/databases')])
      .then(([volumeRes, dbRes]) => {
        if (cancelled) return
        setVolumes(volumeRes.data ?? [])
        setDatabaseIds(new Set((dbRes.data?.items ?? []).map((d) => d.id)))
      })
      .catch((err) => {
        if (cancelled) return
        setError(err)
        setVolumes([])
      })

    return () => {
      cancelled = true
    }
  }, [])

  if (volumes === null) {
    return (
      <p className="flex items-center gap-2 text-sm text-muted-foreground">
        <Loader2 className="size-4 animate-spin text-transitional" />
        Loading volumes…
      </p>
    )
  }

  const reserved = volumes.reduce((n, v) => n + bytesToGiB(v.sizeAllocationBytes), 0)
  const measured = volumes.reduce((n, v) => n + bytesToGiB(v.lastMeasuredBytes ?? 0), 0)

  // The API marks a volume orphaned with a timestamp rather than a flag, which
  // also says when it was noticed.
  const orphans = volumes.filter((v) => v.orphanedAt)

  return (
    <div className="mx-auto max-w-6xl">
      <PageHeader
        title="Storage"
        description={`${volumes.length} volume${volumes.length === 1 ? '' : 's'} · ${measured.toFixed(1)} of ${reserved.toFixed(0)} GiB reserved is in use.`}
      />

      {error != null && <ProblemBanner error={error} />}

      {orphans.length > 0 && (
        <div className="mb-4 rounded-lg border border-degraded/40 bg-degraded-soft/50 px-4 py-3">
          <p className="text-sm font-medium text-degraded">
            {orphans.length} volume{orphans.length > 1 ? 's' : ''} no longer attached to a workload
          </p>
          <p className="mt-0.5 text-sm text-foreground/80">
            {orphans.map((v) => v.name).join(', ')} — still occupying disk. Delete them if the data is gone.
          </p>
        </div>
      )}

      {volumes.length === 0 ? (
        <EmptyState
          icon={HardDrive}
          title="No volumes yet"
          description="Volumes appear when you provision a database or attach storage to an application."
        />
      ) : (
        <Panel bodyClassName="p-0">
          <ul className="divide-y divide-border">
            {volumes.map((v) => {
              const orphaned = Boolean(v.orphanedAt)
              const href = orphaned
                ? undefined
                : databaseIds.has(v.workloadId)
                  ? `/databases/${v.workloadId}`
                  : `/applications/${v.workloadId}`

              return (
                <li key={v.id} className="flex flex-col gap-3 p-4 sm:flex-row sm:items-center sm:gap-6">
                  <div className="flex min-w-0 flex-1 items-start gap-3">
                    <span className="grid size-9 shrink-0 place-items-center rounded-md bg-secondary text-muted-foreground">
                      {orphaned ? <Unplug className="size-4 text-degraded" /> : <HardDrive className="size-4" />}
                    </span>
                    <div className="min-w-0">
                      <p className="truncate font-mono text-sm text-foreground">{v.name}</p>
                      <p className="mt-0.5 flex flex-wrap items-center gap-x-2 text-xs text-muted-foreground">
                        <Mono>{v.purpose}</Mono>
                        {v.measuredAt && (
                          <>
                            <span aria-hidden>·</span>
                            <span>measured {formatRelative(v.measuredAt)}</span>
                          </>
                        )}
                      </p>
                      {orphaned ? (
                        <p className="mt-1 text-xs text-degraded">
                          orphaned {v.orphanedAt ? formatRelative(v.orphanedAt) : ''}
                        </p>
                      ) : (
                        href && (
                          <Link href={href} className="mt-1 inline-block font-mono text-xs text-primary hover:underline">
                            {v.workloadSlug}
                          </Link>
                        )
                      )}
                    </div>
                  </div>
                  <div className="w-full sm:w-56">
                    <ResourceMeter
                      label="Used"
                      used={bytesToGiB(v.lastMeasuredBytes ?? 0)}
                      limit={bytesToGiB(v.sizeAllocationBytes)}
                      unit="GiB"
                    />
                  </div>
                </li>
              )
            })}
          </ul>
        </Panel>
      )}
    </div>
  )
}
