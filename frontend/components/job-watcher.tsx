'use client'

import { useEffect, useState } from 'react'
import { Check, Circle, Loader2, X } from 'lucide-react'

import { subscribeJobEvents, type JobAccepted, type JobDto, type JobStepDto } from '@/lib/api/jobs'
import { cn } from '@/lib/utils'

/**
 * Live job progress from eventsUrl. Never a bare spinner — each step is named
 * as the API reports it. Reconnects resume via Last-Event-ID.
 */
export function JobWatcher({
  job,
  onDone,
}: {
  job: JobAccepted
  onDone?: (final: JobDto) => void
}) {
  const [steps, setSteps] = useState<JobStepDto[]>([])
  const [current, setCurrent] = useState<string | null>(null)
  const [status, setStatus] = useState<string>('queued')
  const [error, setError] = useState<string | null>(null)
  const [progress, setProgress] = useState<number>(0)

  useEffect(() => {
    return subscribeJobEvents(job.eventsUrl, (ev) => {
      if (ev.name === 'job.step') {
        setSteps((prev) => {
          const next = prev.filter((s) => s.sequence !== ev.data.sequence)
          next.push(ev.data)
          return next.sort((a, b) => Number(a.sequence) - Number(b.sequence))
        })
        setCurrent(ev.data.name)
      }
      if (ev.name === 'job.updated' || ev.name === 'job.completed') {
        setStatus(ev.data.status)
        setCurrent(ev.data.currentStep ?? null)
        setProgress(Number(ev.data.progressPercent ?? 0))
        setError(ev.data.errorMessage ?? null)
        if (ev.data.steps?.length) setSteps(ev.data.steps)
        if (ev.name === 'job.completed') onDone?.(ev.data)
      }
    })
  }, [job.eventsUrl, onDone])

  const failed = status === 'failed'
  const succeeded = status === 'succeeded'

  return (
    <div className="rounded-lg border border-border bg-card">
      <div className="flex items-center justify-between gap-3 border-b border-border px-4 py-3">
        <div>
          <p className="font-mono text-xs text-muted-foreground">{job.jobType}</p>
          <p className="text-sm font-medium text-foreground">{current ?? 'Queued'}</p>
        </div>
        <span
          className={cn(
            'font-mono text-xs',
            failed ? 'text-failed' : succeeded ? 'text-running' : 'text-transitional',
          )}
        >
          {status} · {progress}%
        </span>
      </div>
      <ol className="flex flex-col px-4 py-3">
        {steps.length === 0 && (
          <li className="flex items-center gap-2 text-sm text-muted-foreground">
            <Loader2 className="size-4 animate-spin text-transitional" />
            Waiting for the first step…
          </li>
        )}
        {steps.map((step, i) => {
          const last = i === steps.length - 1
          const running = last && !succeeded && !failed
          return (
            <li key={String(step.sequence)} className="relative flex gap-3 pb-3 last:pb-0">
              {!last && <span className="absolute left-[0.6875rem] top-6 h-[calc(100%-1rem)] w-px bg-border" />}
              <span
                className={cn(
                  'grid size-6 shrink-0 place-items-center rounded-full border',
                  failed && last
                    ? 'border-failed/50 bg-failed/15 text-failed'
                    : running
                      ? 'border-transitional/50 bg-transitional/15 text-transitional'
                      : 'border-running/50 bg-running/15 text-running',
                )}
              >
                {failed && last ? (
                  <X className="size-3.5" />
                ) : running ? (
                  <Loader2 className="size-3.5 animate-spin" />
                ) : (
                  <Check className="size-3.5" />
                )}
              </span>
              <div className="min-w-0 pt-0.5">
                <p className="text-sm text-foreground">{step.name}</p>
                {step.message && <p className="font-mono text-xs text-muted-foreground">{step.message}</p>}
              </div>
            </li>
          )
        })}
      </ol>
      {error && (
        <p className="border-t border-failed/40 bg-failed-soft/40 px-4 py-2 text-sm text-failed">{error}</p>
      )}
    </div>
  )
}

export function JobIdle() {
  return (
    <div className="flex items-center gap-2 text-sm text-muted-foreground">
      <Circle className="size-3" />
      No job running
    </div>
  )
}
