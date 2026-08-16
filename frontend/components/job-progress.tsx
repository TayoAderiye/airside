'use client'

import { useEffect, useRef, useState } from 'react'
import { Check, X, Loader2, Circle } from 'lucide-react'
import type { JobStep } from '@/lib/api/types'
import { cn } from '@/lib/utils'

/**
 * Live job progress. In production this is driven by the jobs SignalR hub
 * (see lib/api/types — assumed contract). Here a timer advances the steps so
 * the operator sees the same running -> succeeded transitions, including a
 * simulated failure path when `failAt` is set.
 */
export function JobProgress({
  steps: initialSteps,
  stepMs = 1400,
  failAt,
  onDone,
}: {
  steps: JobStep[]
  stepMs?: number
  failAt?: number
  onDone?: (ok: boolean) => void
}) {
  const [steps, setSteps] = useState<JobStep[]>(initialSteps)
  const doneRef = useRef(false)

  useEffect(() => {
    const timer = setInterval(() => {
      setSteps((prev) => {
        const runningIdx = prev.findIndex((s) => s.state === 'running')
        if (runningIdx === -1) return prev
        const next = prev.map((s) => ({ ...s }))

        if (failAt != null && runningIdx === failAt) {
          next[runningIdx].state = 'failed'
          next[runningIdx].error = 'exit code 1 — see logs for detail'
          if (!doneRef.current) {
            doneRef.current = true
            onDone?.(false)
          }
          return next
        }

        next[runningIdx].state = 'succeeded'
        const following = next[runningIdx + 1]
        if (following) {
          following.state = 'running'
        } else if (!doneRef.current) {
          doneRef.current = true
          onDone?.(true)
        }
        return next
      })
    }, stepMs)
    return () => clearInterval(timer)
  }, [stepMs, failAt, onDone])

  return (
    <ol className="flex flex-col">
      {steps.map((step, i) => {
        const isLast = i === steps.length - 1
        return (
          <li key={step.id} className="relative flex gap-3 pb-4 last:pb-0">
            {!isLast && (
              <span
                className={cn(
                  'absolute left-[0.6875rem] top-6 h-[calc(100%-1rem)] w-px',
                  step.state === 'succeeded' ? 'bg-running/50' : 'bg-border',
                )}
                aria-hidden
              />
            )}
            <StepIcon state={step.state} />
            <div className="flex min-w-0 flex-col pt-0.5">
              <span
                className={cn(
                  'text-sm',
                  step.state === 'pending' ? 'text-muted-foreground' : 'text-foreground',
                  step.state === 'running' && 'font-medium',
                )}
              >
                {step.label}
              </span>
              {step.error && <span className="font-mono text-xs text-failed">{step.error}</span>}
              {step.state === 'running' && (
                <span className="font-mono text-xs text-transitional">in progress…</span>
              )}
            </div>
          </li>
        )
      })}
    </ol>
  )
}

function StepIcon({ state }: { state: JobStep['state'] }) {
  const base = 'flex size-6 shrink-0 items-center justify-center rounded-full border'
  if (state === 'succeeded')
    return (
      <span className={cn(base, 'border-running/50 bg-running/15 text-running')}>
        <Check className="size-3.5" />
      </span>
    )
  if (state === 'failed')
    return (
      <span className={cn(base, 'border-failed/50 bg-failed/15 text-failed')}>
        <X className="size-3.5" />
      </span>
    )
  if (state === 'running')
    return (
      <span className={cn(base, 'border-transitional/50 bg-transitional/15 text-transitional')}>
        <Loader2 className="size-3.5 animate-spin" />
      </span>
    )
  return (
    <span className={cn(base, 'border-border bg-secondary text-muted-foreground')}>
      <Circle className="size-2 fill-current" />
    </span>
  )
}
