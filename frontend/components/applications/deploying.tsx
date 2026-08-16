'use client'

import Link from 'next/link'
import { useSearchParams } from 'next/navigation'

import { JobRun } from '@/components/job-run'
import { Panel, PageHeader } from '@/components/ui/panel'
import { BackLink } from '@/components/ui/back-link'
import { buttonVariants } from '@/components/ui/button'
import { cn } from '@/lib/utils'

/**
 * Follows a deployment job.
 *
 * The application already exists by the time this screen renders — creating it
 * is synchronous, and only the deployment is a job. So a failure here means a
 * deployment that did not take, not an application that was never made, and the
 * link out goes to the application either way.
 */
export function AppDeploying() {
  const params = useSearchParams()
  const jobId = params.get('job')
  const appId = params.get('app')

  if (!jobId) {
    return (
      <Panel title="No job">
        <p className="text-sm text-muted-foreground">
          This page follows a deployment job and none was named. Start from the
          application list.
        </p>
        <Link href="/applications" className={cn(buttonVariants({ variant: 'default' }), 'mt-3')}>
          Applications
        </Link>
      </Panel>
    )
  }

  return (
    <div className="flex flex-col gap-5">
      <BackLink href="/applications">Applications</BackLink>
      <PageHeader
        title="Deploying"
        description="The new container has to pass its health check before traffic moves and the old one stops."
      />

      <div className="max-w-2xl">
        <JobRun
          jobId={jobId}
          destination={(final) => {
            const id = final.workloadId ?? appId
            return id
              ? { href: `/applications/${id}`, label: 'View application' }
              : { href: '/applications', label: 'Back to applications' }
          }}
        />
      </div>
    </div>
  )
}
