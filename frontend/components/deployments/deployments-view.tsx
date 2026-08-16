'use client'

import { useState } from 'react'
import Link from 'next/link'
import { GitCommitHorizontal, RotateCcw, Timer } from 'lucide-react'

import { ConfirmDialog } from '@/components/confirm-dialog'
import { StatusBadge } from '@/components/status-badge'
import { Button } from '@/components/ui/button'
import { PageHeader, Panel } from '@/components/ui/panel'
import { deployments } from '@/lib/api/mock'
import type { Deployment } from '@/lib/api/types'
import { formatRelative } from '@/lib/status'

export function DeploymentsView() {
  const [target, setTarget] = useState<Deployment | null>(null)
  const sorted = [...deployments].sort((a, b) => +new Date(b.startedAt) - +new Date(a.startedAt))
  const current = target ? deployments.find((d) => d.appId === target.appId && d.isCurrent) : undefined

  return (
    <div className="flex flex-col gap-5">
      <PageHeader title="Deployments" description="Every rollout across all applications on this host, newest first." />

      <Panel bodyClassName="p-0">
        <ul className="divide-y divide-border">
          {sorted.map((d) => (
            <li key={d.id} className="flex flex-col gap-3 p-4 sm:flex-row sm:items-center sm:gap-4">
              <div className="flex min-w-0 flex-1 items-start gap-3">
                <GitCommitHorizontal className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
                <div className="min-w-0">
                  <div className="flex flex-wrap items-center gap-2">
                    <Link
                      href={`/applications/${d.appId}`}
                      className="font-display text-sm font-semibold text-foreground hover:text-accent"
                    >
                      {d.appName}
                    </Link>
                    <span className="font-mono text-xs text-foreground">{d.sha}</span>
                    <span className="rounded bg-secondary px-1.5 py-0.5 font-mono text-[11px] text-muted-foreground">
                      {d.branch}
                    </span>
                    {d.isCurrent && (
                      <span className="rounded bg-running/15 px-1.5 py-0.5 text-[11px] font-medium text-running">
                        current
                      </span>
                    )}
                  </div>
                  <p className="mt-0.5 truncate text-sm text-foreground">{d.message}</p>
                  <p className="font-mono text-xs text-muted-foreground">
                    {d.author} · {formatRelative(d.startedAt)}
                  </p>
                </div>
              </div>
              <div className="flex shrink-0 items-center gap-3 pl-7 sm:pl-0">
                {d.durationSeconds != null && (
                  <span className="inline-flex items-center gap-1 font-mono text-xs text-muted-foreground">
                    <Timer className="size-3" />
                    {d.durationSeconds}s
                  </span>
                )}
                <StatusBadge state={d.state} />
                {!d.isCurrent && d.state !== 'Failed' && (
                  <Button variant="outline" size="sm" onClick={() => setTarget(d)}>
                    <RotateCcw className="size-3.5" /> Roll back
                  </Button>
                )}
              </div>
            </li>
          ))}
        </ul>
      </Panel>

      <ConfirmDialog
        open={target != null}
        onOpenChange={(open) => !open && setTarget(null)}
        tone="warn"
        title="Roll back"
        confirmLabel={`Roll back to ${target?.sha ?? ''}`}
        requireTyped={target?.sha}
        description={
          target ? (
            <div className="flex flex-col gap-2">
              <p>
                {target.appName} will leave {current?.sha ?? 'the current release'} and run {target.sha}. Type the
                target SHA so the wrong commit cannot be selected by accident.
              </p>
              <div className="grid grid-cols-2 gap-2 rounded-md border border-border bg-secondary/40 p-2 font-mono text-xs">
                <div>
                  <p className="text-[10px] uppercase tracking-wide text-muted-foreground">Current</p>
                  <p className="text-foreground">{current?.sha ?? '—'}</p>
                  <p className="mt-1 text-muted-foreground">{current?.message}</p>
                </div>
                <div>
                  <p className="text-[10px] uppercase tracking-wide text-degraded">Roll back to</p>
                  <p className="text-foreground">{target.sha}</p>
                  <p className="mt-1 text-muted-foreground">{target.message}</p>
                  <p className="text-muted-foreground">
                    {target.branch} · {target.author}
                  </p>
                </div>
              </div>
            </div>
          ) : null
        }
      />
    </div>
  )
}
