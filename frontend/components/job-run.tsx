'use client'

import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import { CheckCircle2, Loader2 } from 'lucide-react'

import { JobWatcher } from '@/components/job-watcher'
import { ProblemBanner } from '@/components/problem-banner'
import { buttonVariants } from '@/components/ui/button'
import { client } from '@/lib/api/client'
import type { JobAccepted, JobDto } from '@/lib/api/jobs'
import { cn } from '@/lib/utils'

/**
 * Follows one job to its end, from nothing but its id.
 *
 * The id is the only thing carried in the URL, and that is deliberate. The
 * screens this replaced passed a name and an engine as query parameters and
 * animated a fixed list of steps against them — so the page could not be
 * refreshed, could not be linked to, and could not report a failure, because it
 * was never connected to the work it claimed to be showing.
 *
 * Reloading here re-fetches the job and picks it up wherever it actually is,
 * including after it has finished.
 */
export function JobRun({
  jobId,
  destination,
}: {
  jobId: string

  /** Where to send the operator once the job succeeds. */
  destination: (final: JobDto) => { href: string; label: string } | null
}) {
  const [job, setJob] = useState<JobAccepted | null>(null)
  const [final, setFinal] = useState<JobDto | null>(null)
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    let cancelled = false

    async function load() {
      try {
        const res = await client.GET('/api/v1/jobs/{id}', { params: { path: { id: jobId } } })
        if (cancelled || !res.data) return

        // Rebuilt from the id rather than threaded through the URL. These two
        // paths are part of the contract, and a job id in an address bar is
        // worth something to an operator where a serialised object is not.
        setJob({
          jobId: res.data.id,
          jobType: res.data.type,
          workloadId: res.data.workloadId,
          statusUrl: `/api/v1/jobs/${res.data.id}`,
          eventsUrl: `/api/v1/jobs/${res.data.id}/events`,
        })

        // Already over by the time the page loaded — a fast job, or a reload
        // long afterwards. Without this the screen would wait on a stream that
        // has nothing left to say.
        if (res.data.status === 'succeeded' || res.data.status === 'failed') {
          setFinal(res.data)
        }
      } catch (err) {
        if (!cancelled) setError(err)
      }
    }

    void load()

    return () => {
      cancelled = true
    }
  }, [jobId])

  const onDone = useCallback((completed: JobDto) => setFinal(completed), [])

  if (error != null) {
    return <ProblemBanner error={error} />
  }

  if (!job) {
    return (
      <div className="flex items-center gap-2 text-sm text-muted-foreground">
        <Loader2 className="size-4 animate-spin text-transitional" />
        Finding the job…
      </div>
    )
  }

  const target = final?.status === 'succeeded' ? destination(final) : null

  return (
    <div className="flex flex-col gap-4">
      <JobWatcher job={job} onDone={onDone} />

      {target && (
        <div className="flex flex-col gap-3 rounded-md border border-running/40 bg-running/10 p-3">
          <p className="flex items-center gap-2 text-sm font-medium text-running">
            <CheckCircle2 className="size-4" />
            Done.
          </p>
          <Link href={target.href} className={cn(buttonVariants({ variant: 'default' }), 'w-full')}>
            {target.label}
          </Link>
        </div>
      )}

      {final?.status === 'failed' && (
        // The job's own message, not a generic apology. It names the step that
        // failed, which is the only thing that helps.
        <div className="rounded-md border border-failed/40 bg-failed-soft/40 p-3">
          <p className="font-mono text-[11px] text-failed">{final.errorCode ?? 'job.failed'}</p>
          <p className="text-sm text-foreground">
            {final.errorMessage ?? 'The job failed without reporting a reason.'}
          </p>
        </div>
      )}
    </div>
  )
}
