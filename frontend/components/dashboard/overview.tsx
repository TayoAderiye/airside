'use client'

import { useEffect, useState } from 'react'
import Link from 'next/link'
import { AlertTriangle } from 'lucide-react'

import { AllocationRail } from '@/components/allocation-rail'
import { ProblemBanner } from '@/components/problem-banner'
import { StatusBadge } from '@/components/status-badge'
import { PageHeader, Panel, Mono } from '@/components/ui/panel'
import { Skeleton } from '@/components/ui/skeleton'
import { WarningsList } from '@/components/warnings-list'
import { client } from '@/lib/api/client'
import type { components } from '@/lib/api/schema'
import { apiState, cpuRail, memoryRail, storageRail } from '@/lib/api/units'

type Host = components['schemas']['HostDto']
type App = components['schemas']['ApplicationSummaryDto']
type Db = components['schemas']['DatabaseSummaryDto']
type Note = components['schemas']['NotificationDto']

export function Overview() {
  const [host, setHost] = useState<Host | null>(null)
  const [apps, setApps] = useState<App[]>([])
  const [dbs, setDbs] = useState<Db[]>([])
  const [notes, setNotes] = useState<Note[]>([])
  const [error, setError] = useState<unknown>(null)
  const [ready, setReady] = useState(false)

  useEffect(() => {
    Promise.all([
      client.GET('/api/v1/host'),
      client.GET('/api/v1/applications'),
      client.GET('/api/v1/databases'),
      client.GET('/api/v1/notifications', { params: { query: { includeResolved: false } } }),
    ])
      .then(([h, a, d, n]) => {
        setHost(h.data ?? null)
        setApps(a.data?.items ?? [])
        setDbs(d.data?.items ?? [])
        setNotes(n.data ?? [])
      })
      .catch(setError)
      .finally(() => setReady(true))
  }, [])

  const problems = [
    ...dbs.filter((d) => ['failed', 'unhealthy'].includes(d.state)).map((d) => ({ name: d.slug, state: d.state })),
    ...apps.filter((a) => ['failed', 'unhealthy'].includes(a.state)).map((a) => ({ name: a.slug, state: a.state })),
  ]

  return (
    <div className="mx-auto max-w-6xl">
      <PageHeader title="Overview" description="Host capacity versus allocated, and every workload on this machine." />
      {error != null && (
        <div className="mb-3">
          <ProblemBanner error={error} />
        </div>
      )}

      {notes.length > 0 && (
        <div className="mb-3 rounded-md border border-degraded/30 bg-degraded-soft/50 px-3 py-2.5">
          <p className="text-sm font-medium text-degraded">{notes.length} unresolved notification{notes.length > 1 ? 's' : ''}</p>
          <p className="mt-0.5 text-sm text-foreground/80">
            {notes.slice(0, 3).map((n) => n.title).join(' · ')}
          </p>
          <Link href="/notifications" className="mt-1 inline-block text-xs text-primary hover:underline">
            Open notifications
          </Link>
        </div>
      )}

      {problems.length > 0 && (
        <div className="mb-3 flex items-start gap-2.5 rounded-md border border-degraded/30 bg-degraded-soft/50 px-3 py-2.5">
          <AlertTriangle className="mt-0.5 size-3.5 shrink-0 text-degraded" />
          <p className="text-sm text-foreground">
            {problems.map((p, i) => (
              <span key={p.name}>
                {i > 0 && ' · '}
                <span className="font-mono">{p.name}</span> is {p.state}
              </span>
            ))}
          </p>
        </div>
      )}

      {!ready && <OverviewSkeleton />}

      {ready && host && (
        <>
          <WarningsList warnings={host.warnings} />
          <section className="mb-4 mt-3">
            <Panel
              title="Capacity"
              description="Capacity is what the host has. Allocated is the sum of limits. Used is consumption right now."
            >
              <div className="flex flex-col gap-4">
                <AllocationRail label="CPU" triple={cpuRail(host.capacity, host.allocated, host.used)} />
                <AllocationRail label="Memory" triple={memoryRail(host.capacity, host.allocated, host.used)} />
                <AllocationRail label="Storage" triple={storageRail(host.capacity, host.allocated, host.used)} />
              </div>
              <p className="mt-3 font-mono text-[11px] text-muted-foreground">
                {host.operatingSystem ?? 'os unknown'} · kernel {host.kernelVersion ?? '—'} · docker{' '}
                {host.dockerApiVersion ?? 'unreachable'}
              </p>
            </Panel>
          </section>
        </>
      )}

      {ready && (
        <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
          <Panel title="Databases" bodyClassName="p-0">
            <WorkloadList
              items={dbs.map((d) => ({
                id: d.id,
                href: `/databases/${d.id}`,
                name: d.displayName || d.slug,
                meta: `${d.engine} ${d.version}`,
                state: d.state,
              }))}
              empty="No databases. Provision one."
            />
          </Panel>
          <Panel title="Applications" bodyClassName="p-0">
            <WorkloadList
              items={apps.map((a) => ({
                id: a.id,
                href: `/applications/${a.id}`,
                name: a.displayName || a.slug,
                meta: a.sourceKind,
                state: a.state,
              }))}
              empty="No applications. Deploy one."
            />
          </Panel>
        </div>
      )}
    </div>
  )
}

function WorkloadList({
  items,
  empty,
}: {
  items: { id: string; href: string; name: string; meta: string; state: string }[]
  empty: string
}) {
  if (items.length === 0) {
    return <p className="px-3 py-5 text-sm text-muted-foreground">{empty}</p>
  }

  return (
    <ul className="divide-y divide-border">
      {items.map((item) => (
        <li key={item.id}>
          <Link
            href={item.href}
            className="flex items-center justify-between gap-3 px-3 py-2 transition-colors hover:bg-secondary/50"
          >
            <div className="min-w-0">
              <p className="truncate text-sm font-medium">{item.name}</p>
              <Mono className="text-muted-foreground">{item.meta}</Mono>
            </div>
            <StatusBadge state={apiState(item.state)} />
          </Link>
        </li>
      ))}
    </ul>
  )
}

function OverviewSkeleton() {
  return (
    <div aria-busy="true" aria-live="polite">
      <span className="sr-only">Loading overview</span>
      <section className="mb-4">
        <Panel title="Capacity">
          <div className="flex flex-col gap-4">
            <RailSkeleton />
            <RailSkeleton />
            <RailSkeleton />
          </div>
        </Panel>
      </section>
      <div className="grid grid-cols-1 gap-3 lg:grid-cols-2">
        <Panel title="Databases" bodyClassName="p-0">
          <RowSkeletons />
        </Panel>
        <Panel title="Applications" bodyClassName="p-0">
          <RowSkeletons />
        </Panel>
      </div>
    </div>
  )
}

function RailSkeleton() {
  return (
    <div className="flex flex-col gap-2">
      <div className="flex justify-between">
        <Skeleton className="h-3.5 w-16" />
        <Skeleton className="h-3 w-24" />
      </div>
      <Skeleton className="h-5 w-full rounded-md" />
    </div>
  )
}

function RowSkeletons() {
  return (
    <ul className="divide-y divide-border">
      {Array.from({ length: 4 }, (_, i) => (
        <li key={i} className="flex items-center justify-between gap-3 px-3 py-2">
          <div className="flex flex-col gap-1.5">
            <Skeleton className="h-3.5 w-32" />
            <Skeleton className="h-2.5 w-20" />
          </div>
          <Skeleton className="h-5 w-16 rounded-md" />
        </li>
      ))}
    </ul>
  )
}
