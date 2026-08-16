'use client'

import { useEffect, useState } from 'react'
import { Loader2 } from 'lucide-react'

import { AllocationRail } from '@/components/allocation-rail'
import { ProblemBanner } from '@/components/problem-banner'
import { Mono, PageHeader, Panel, StatItem } from '@/components/ui/panel'
import { client } from '@/lib/api/client'
import { cpuRail, memoryRail, storageRail } from '@/lib/api/units'
import type { components } from '@/lib/api/schema'
import { formatRelative } from '@/lib/status'

type Host = components['schemas']['HostDto']
type SystemInfo = components['schemas']['SystemInfoDto']

export function ServersView() {
  const [host, setHost] = useState<Host | null>(null)
  const [info, setInfo] = useState<SystemInfo | null>(null)
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    let cancelled = false

    Promise.all([client.GET('/api/v1/host'), client.GET('/api/v1/system/info')])
      .then(([hostRes, infoRes]) => {
        if (cancelled) return
        setHost(hostRes.data ?? null)
        setInfo(infoRes.data ?? null)
      })
      .catch((err) => {
        if (!cancelled) setError(err)
      })

    return () => {
      cancelled = true
    }
  }, [])

  if (error != null && !host) {
    return <ProblemBanner error={error} />
  }

  if (!host) {
    return (
      <p className="flex items-center gap-2 text-sm text-muted-foreground">
        <Loader2 className="size-4 animate-spin text-transitional" />
        Loading host…
      </p>
    )
  }

  return (
    <div className="mx-auto max-w-6xl">
      <PageHeader
        title="Servers"
        description="This host. Airside manages a single Linux machine — there is no fleet view."
      />

      <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="font-display text-lg font-semibold text-foreground">{host.name}</h2>
          <Mono className="text-muted-foreground">
            {host.operatingSystem ?? 'os unknown'}
            {host.lastDiscoveredAt ? ` · discovered ${formatRelative(host.lastDiscoveredAt)}` : ''}
          </Mono>
        </div>
      </div>

      {host.warnings.length > 0 && (
        <div className="mb-4 flex flex-col gap-2">
          {host.warnings.map((w) => (
            <div key={w.code} className="rounded-lg border border-degraded/40 bg-degraded-soft/50 px-4 py-3">
              <p className="font-mono text-[11px] text-degraded">{w.code}</p>
              <p className="text-sm text-foreground">{w.message}</p>
            </div>
          ))}
        </div>
      )}

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-3">
        <Panel
          title="Capacity"
          className="lg:col-span-2"
          description="Capacity is what the host has. Allocated is the sum of limits. Used is consumption right now."
        >
          <div className="flex flex-col gap-5">
            <AllocationRail label="CPU" triple={cpuRail(host.capacity, host.allocated, host.used)} />
            <AllocationRail label="Memory" triple={memoryRail(host.capacity, host.allocated, host.used)} />
            <AllocationRail label="Storage" triple={storageRail(host.capacity, host.allocated, host.used)} />
          </div>
        </Panel>

        <Panel title="Runtime">
          <div className="flex flex-col gap-3">
            <StatItem label="Kernel" value={host.kernelVersion ?? '—'} mono />
            <StatItem label="Docker API" value={host.dockerApiVersion ?? 'unreachable'} mono />
            <StatItem
              label="Daemon"
              value={info?.runtimeAvailable ? 'connected' : 'unreachable'}
              tone={info?.runtimeAvailable ? 'good' : 'bad'}
            />
            <StatItem label="Storage limits" value={host.storageEnforcement} mono />
            {info && (
              <>
                <StatItem label="Airside" value={info.version} mono />
                <StatItem label="Store" value={info.storeProvider} mono />

                {/* The API's start time, not the machine's. HostDto carries no
                    host uptime, and labelling this "uptime" would say something
                    about the box that Airside does not know. */}
                <StatItem label="API started" value={formatRelative(info.startedAt)} mono />
              </>
            )}
          </div>
        </Panel>
      </div>
    </div>
  )
}
