'use client'

import { useState } from 'react'
import { Database, Rocket } from 'lucide-react'
import type { AppSummary, DatabaseSummary } from '@/lib/api/types'
import { PageHeader, Panel } from '@/components/ui/panel'
import { StatusDot } from '@/components/status-badge'
import { LogStream } from '@/components/logs/log-stream'
import { cn } from '@/lib/utils'

type Source = { id: string; name: string; kind: 'database' | 'application'; state: DatabaseSummary['state'] }

export function MonitoringView({ databases, apps }: { databases: DatabaseSummary[]; apps: AppSummary[] }) {
  const sources: Source[] = [
    ...apps.map((a) => ({ id: a.id, name: a.name, kind: 'application' as const, state: a.state })),
    ...databases.map((d) => ({ id: d.id, name: d.name, kind: 'database' as const, state: d.state })),
  ]
  const [selected, setSelected] = useState<Source>(sources[0])

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title="Monitoring"
        description="Live log streams from every workload on this host. Streams are delivered over the logs hub."
      />

      <div className="grid grid-cols-1 gap-5 lg:grid-cols-[240px_1fr]">
        {/* Source picker */}
        <Panel title="Workloads" bodyClassName="p-0">
          <ul className="flex flex-col">
            {sources.map((s) => {
              const active = s.id === selected.id
              const Icon = s.kind === 'application' ? Rocket : Database
              return (
                <li key={s.id}>
                  <button
                    type="button"
                    onClick={() => setSelected(s)}
                    aria-current={active}
                    className={cn(
                      'flex w-full items-center gap-2.5 border-l-2 px-3 py-2.5 text-left transition-colors',
                      active
                        ? 'border-accent bg-accent/10 text-foreground'
                        : 'border-transparent text-muted-foreground hover:bg-secondary/60 hover:text-foreground',
                    )}
                  >
                    <Icon className="size-4 shrink-0" />
                    <span className="min-w-0 flex-1 truncate font-mono text-xs">{s.name}</span>
                    <StatusDot state={s.state} />
                  </button>
                </li>
              )
            })}
          </ul>
        </Panel>

        {/* Stream — keyed by source so switching restarts the connection */}
        <Panel
          title={
            <span className="flex items-center gap-2">
              {selected.kind === 'application' ? <Rocket className="size-4" /> : <Database className="size-4" />}
              <span className="font-mono">{selected.name}</span>
            </span>
          }
          description={`Live tail · ${selected.kind}`}
          bodyClassName="p-0"
        >
          <LogStream key={selected.id} source={selected.name} height="34rem" />
        </Panel>
      </div>
    </div>
  )
}
