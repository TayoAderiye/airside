'use client'

import { useState } from 'react'
import Link from 'next/link'
import { useSearchParams } from 'next/navigation'
import { CheckCircle2 } from 'lucide-react'
import type { JobStep } from '@/lib/api/types'
import { Panel, PageHeader } from '@/components/ui/panel'
import { JobProgress } from '@/components/job-progress'
import { LogStream } from '@/components/logs/log-stream'
import { buttonVariants } from '@/components/ui/button'
import { cn } from '@/lib/utils'

const STEPS: JobStep[] = [
  { id: 'clone', label: 'Clone repository', state: 'running' },
  { id: 'build', label: 'Build image', state: 'pending' },
  { id: 'push', label: 'Push to registry', state: 'pending' },
  { id: 'rollout', label: 'Roll out replicas', state: 'pending' },
  { id: 'health', label: 'Health check', state: 'pending' },
]

export function AppDeploying() {
  const params = useSearchParams()
  const name = params.get('name') ?? 'application'
  const [done, setDone] = useState(false)

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title={`Deploying ${name}`}
        description="Building and rolling out. This view streams from the deploy job and its container logs."
      />

      <div className="grid grid-cols-1 gap-5 lg:grid-cols-[360px_1fr]">
        <div className="flex flex-col gap-4">
          <Panel title="Deploy pipeline">
            <JobProgress steps={STEPS} stepMs={1400} onDone={() => setDone(true)} />
          </Panel>

          {done && (
            <Panel className="border-running/40 bg-running/5">
              <div className="flex flex-col items-center gap-3 py-2 text-center">
                <CheckCircle2 className="size-8 text-running" />
                <div>
                  <p className="font-display text-sm font-semibold text-foreground">Deployment live</p>
                  <p className="text-xs text-muted-foreground">All replicas healthy and receiving traffic.</p>
                </div>
                <Link href="/applications/app_api" className={cn(buttonVariants({ variant: 'default' }), 'w-full')}>
                  View application
                </Link>
              </div>
            </Panel>
          )}
        </div>

        <Panel title="Build & runtime logs" bodyClassName="p-0" className="min-h-[440px]">
          <LogStream source="build" height="27.5rem" />
        </Panel>
      </div>
    </div>
  )
}
