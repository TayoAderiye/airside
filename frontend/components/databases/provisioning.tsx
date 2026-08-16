'use client'

import { useState } from 'react'
import Link from 'next/link'
import { useSearchParams } from 'next/navigation'
import { CheckCircle2 } from 'lucide-react'
import { Panel } from '@/components/ui/panel'
import { JobProgress } from '@/components/job-progress'
import { LogStream } from '@/components/logs/log-stream'
import { EngineGlyph, engineLabel } from '@/components/engine'
import { buttonVariants } from '@/components/ui/button'
import type { DatabaseEngine, JobStep } from '@/lib/api/types'
import { cn } from '@/lib/utils'

const PROVISION_STEPS: JobStep[] = [
  { id: 'reserve', label: 'Reserve host resources', state: 'running' },
  { id: 'volume', label: 'Create data volume', state: 'pending' },
  { id: 'pull', label: 'Pull engine image', state: 'pending' },
  { id: 'start', label: 'Start container', state: 'pending' },
  { id: 'init', label: 'Initialize data directory', state: 'pending' },
  { id: 'ready', label: 'Wait for readiness probe', state: 'pending' },
]

export function Provisioning() {
  const params = useSearchParams()
  const name = params.get('name') ?? 'new-database'
  const engine = (params.get('engine') as DatabaseEngine) ?? 'postgres'
  const [done, setDone] = useState(false)

  return (
    <div className="flex flex-col gap-6">
      <div className="flex items-center gap-3">
        <EngineGlyph engine={engine} className="size-10" />
        <div>
          <h1 className="font-display text-2xl font-semibold text-foreground">{name}</h1>
          <p className="font-mono text-sm text-muted-foreground">
            Provisioning {engineLabel(engine)} · this may take a moment
          </p>
        </div>
      </div>

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-[20rem_minmax(0,1fr)]">
        <Panel title="Steps">
          <JobProgress steps={PROVISION_STEPS} stepMs={1600} onDone={() => setDone(true)} />
          {done && (
            <div className="mt-4 flex flex-col gap-3 rounded-md border border-running/40 bg-running/10 p-3">
              <p className="flex items-center gap-2 text-sm font-medium text-running">
                <CheckCircle2 className="size-4" />
                {name} is running
              </p>
              <Link href={`/databases/db_pg_main`} className={cn(buttonVariants({ variant: 'default' }), 'w-full')}>
                View database
              </Link>
            </div>
          )}
        </Panel>

        <Panel title="Live log" bodyClassName="p-0">
          <LogStream source={name} height="24rem" />
        </Panel>
      </div>
    </div>
  )
}
