'use client'

import { useEffect, useState } from 'react'
import Link from 'next/link'
import { AlertTriangle } from 'lucide-react'

import { AllocationRail } from '@/components/allocation-rail'
import { ProblemBanner } from '@/components/problem-banner'
import { StatusBadge } from '@/components/status-badge'
import { PageHeader, Panel, Mono } from '@/components/ui/panel'
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
  }, [])

  const problems = [
    ...dbs.filter((d) => ['failed', 'unhealthy'].includes(d.state)).map((d) => ({ name: d.slug, state: d.state })),
    ...apps.filter((a) => ['failed', 'unhealthy'].includes(a.state)).map((a) => ({ name: a.slug, state: a.state })),
  ]

  return (
    <div className="mx-auto max-w-6xl">
      <PageHeader title="Overview" description="Host capacity versus allocated, and every workload on this machine." />
      {error != null && <ProblemBanner error={error} />}

      {notes.length > 0 && (
        <div className="mb-4 rounded-lg border border-degraded/40 bg-degraded-soft/50 px-4 py-3">
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
        <div className="mb-4 flex items-start gap-3 rounded-lg border border-degraded/40 bg-degraded-soft/50 px-4 py-3">
          <AlertTriangle className="mt-0.5 size-4 shrink-0 text-degraded" />
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

      {host && (
        <>
          <WarningsList warnings={host.warnings} />
          <section className="mb-6 mt-4">
            <Panel
              title="Capacity"
              description="Capacity is what the host has. Allocated is the sum of limits. Used is consumption right now."
            >
              <div className="flex flex-col gap-5">
                <AllocationRail label="CPU" triple={cpuRail(host.capacity, host.allocated, host.used)} />
                <AllocationRail label="Memory" triple={memoryRail(host.capacity, host.allocated, host.used)} />
                <AllocationRail label="Storage" triple={storageRail(host.capacity, host.allocated, host.used)} />
              </div>
              <p className="mt-3 font-mono text-xs text-muted-foreground">
                {host.operatingSystem ?? 'os unknown'} · kernel {host.kernelVersion ?? '—'} · docker{' '}
                {host.dockerApiVersion ?? 'unreachable'}
              </p>
            </Panel>
          </section>
        </>
      )}

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
        <Panel title="Databases">
          <ul className="divide-y divide-border">
            {dbs.map((d) => (
              <li key={d.id}>
                <Link href={`/databases/${d.id}`} className="flex items-center justify-between gap-3 py-2.5">
                  <div>
                    <p className="text-sm font-medium">{d.displayName || d.slug}</p>
                    <Mono className="text-muted-foreground">
                      {d.engine} {d.version}
                    </Mono>
                  </div>
                  <StatusBadge state={apiState(d.state)} />
                </Link>
              </li>
            ))}
            {dbs.length === 0 && <p className="py-6 text-sm text-muted-foreground">No databases. Provision one.</p>}
          </ul>
        </Panel>
        <Panel title="Applications">
          <ul className="divide-y divide-border">
            {apps.map((a) => (
              <li key={a.id}>
                <Link href={`/applications/${a.id}`} className="flex items-center justify-between gap-3 py-2.5">
                  <div>
                    <p className="text-sm font-medium">{a.displayName || a.slug}</p>
                    <Mono className="text-muted-foreground">{a.sourceKind}</Mono>
                  </div>
                  <StatusBadge state={apiState(a.state)} />
                </Link>
              </li>
            ))}
            {apps.length === 0 && <p className="py-6 text-sm text-muted-foreground">No applications. Deploy one.</p>}
          </ul>
        </Panel>
      </div>
    </div>
  )
}
