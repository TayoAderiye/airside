'use client'

import Link from 'next/link'
import { useSearchParams } from 'next/navigation'

import { JobRun } from '@/components/job-run'
import { Panel } from '@/components/ui/panel'
import { buttonVariants } from '@/components/ui/button'
import { cn } from '@/lib/utils'

/**
 * Follows the provisioning job for a new database.
 *
 * What this replaced animated six fixed step labels on a timer and then linked
 * to a hardcoded id, with no request to the API anywhere in it. It was
 * convincing, and it was the reason a database could appear to provision and
 * then not exist.
 */
export function Provisioning() {
  const jobId = useSearchParams().get('job')

  if (!jobId) {
    return (
      <Panel title="No job">
        <p className="text-sm text-muted-foreground">
          This page follows a provisioning job and none was named. Start from the
          database list.
        </p>
        <Link href="/databases" className={cn(buttonVariants({ variant: 'default' }), 'mt-3')}>
          Databases
        </Link>
      </Panel>
    )
  }

  return (
    <div className="flex flex-col gap-6">
      <div>
        <h1 className="font-display text-2xl font-semibold text-foreground">Provisioning</h1>
        <p className="font-mono text-sm text-muted-foreground">
          Each step is the API&apos;s own. This may take a moment.
        </p>
      </div>

      <div className="max-w-2xl">
        <JobRun
          jobId={jobId}
          destination={(final) =>
            // The workload id comes from the finished job. Nothing else on this
            // page knows it, and nothing should guess it.
            final.workloadId
              ? { href: `/databases/${final.workloadId}`, label: 'View database' }
              : { href: '/databases', label: 'Back to databases' }
          }
        />
      </div>
    </div>
  )
}
