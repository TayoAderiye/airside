'use client'

import { useState } from 'react'
import { RotateCcw, Square, Play, Trash2, ArrowUpRight, GitCommitHorizontal } from 'lucide-react'
import type { AppSummary, Deployment } from '@/lib/api/types'
import { PageHeader, Panel, StatItem } from '@/components/ui/panel'
import { StatusBadge } from '@/components/status-badge'
import { ResourceMeter } from '@/components/resource-meter'
import { AppSourceGlyph, appSourceLabel } from '@/components/app-source'
import { Tabs } from '@/components/ui/tabs'
import { CopyField } from '@/components/ui/copy-field'
import { LogStream } from '@/components/logs/log-stream'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { BackLink } from '@/components/ui/back-link'
import { Button } from '@/components/ui/button'
import { formatRelative } from '@/lib/status'

type Danger = 'stop' | 'delete' | 'rollback' | null

export function AppDetailView({ app, deployments }: { app: AppSummary; deployments: Deployment[] }) {
  const [tab, setTab] = useState('overview')
  const [danger, setDanger] = useState<Danger>(null)
  const [rollbackTarget, setRollbackTarget] = useState<Deployment | null>(null)
  const stopped = app.state === 'Stopped'

  const tabs = [
    { id: 'overview', label: 'Overview' },
    { id: 'deployments', label: 'Deployments', badge: deployments.length },
    { id: 'logs', label: 'Logs' },
    { id: 'env', label: 'Environment' },
    { id: 'danger', label: 'Danger zone' },
  ]

  return (
    <div className="flex flex-col gap-5">
      <BackLink href="/applications">Applications</BackLink>

      <PageHeader
        title={
          <span className="flex items-center gap-3">
            <AppSourceGlyph source={app.source} className="size-9" />
            {app.name}
          </span>
        }
        description={`${appSourceLabel(app)} · ${app.replicas} replica${app.replicas === 1 ? '' : 's'} · port ${app.internalPort || '—'}`}
        actions={
          <div className="flex items-center gap-2">
            <StatusBadge state={app.state} />
            {stopped ? (
              <Button variant="outline" size="sm">
                <Play className="size-3.5" /> Start
              </Button>
            ) : (
              <Button variant="outline" size="sm" onClick={() => setDanger('stop')}>
                <Square className="size-3.5" /> Stop
              </Button>
            )}
          </div>
        }
      />

      <Tabs tabs={tabs} active={tab} onChange={setTab} />

      {tab === 'overview' && (
        <div className="grid grid-cols-1 gap-5 lg:grid-cols-3">
          <Panel title="Resource usage" className="lg:col-span-2">
            <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
              <ResourceMeter label="CPU (all replicas)" used={app.cpu.used} limit={app.cpu.limit} unit="cores" />
              <ResourceMeter label="Memory (all replicas)" used={app.memory.used} limit={app.memory.limit} unit="GiB" />
            </div>
          </Panel>

          <Panel title="Networking">
            <div className="flex flex-col gap-3">
              <StatItem label="Internal port" value={app.internalPort || '—'} mono />
              <StatItem label="Public domain" value={app.domain ?? 'none'} mono />
              <StatItem label="Current SHA" value={app.currentSha?.slice(0, 7) ?? '—'} mono />
            </div>
            {app.domain && (
              <a
                href={`https://${app.domain}`}
                className="mt-3 inline-flex items-center gap-1 font-mono text-xs text-accent hover:underline"
              >
                Open {app.domain}
                <ArrowUpRight className="size-3" />
              </a>
            )}
          </Panel>

          <Panel title="Connection" className="lg:col-span-3">
            <CopyField value={`${app.name}.internal:${app.internalPort}`} />
          </Panel>
        </div>
      )}

      {tab === 'deployments' && (
        <Panel title="Deployment history" bodyClassName="p-0">
          <ul className="divide-y divide-border">
            {deployments.map((d) => (
              <li key={d.id} className="flex flex-col gap-3 p-4 sm:flex-row sm:items-center sm:gap-4">
                <div className="flex min-w-0 flex-1 items-start gap-3">
                  <GitCommitHorizontal className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
                  <div className="min-w-0">
                    <div className="flex items-center gap-2">
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
                      {d.durationSeconds ? ` · ${d.durationSeconds}s` : ''}
                    </p>
                  </div>
                </div>
                <div className="flex shrink-0 items-center gap-3 pl-7 sm:pl-0">
                  <StatusBadge state={d.state} />
                  {!d.isCurrent && d.state !== 'Failed' && (
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => {
                        setRollbackTarget(d)
                        setDanger('rollback')
                      }}
                    >
                      <RotateCcw className="size-3.5" /> Roll back
                    </Button>
                  )}
                </div>
              </li>
            ))}
          </ul>
        </Panel>
      )}

      {tab === 'logs' && (
        <Panel title={`Live logs — ${app.name}`} bodyClassName="p-0">
          <LogStream source={app.name} height="30rem" />
        </Panel>
      )}

      {tab === 'env' && (
        <Panel
          title="Environment variables"
          description="Values are stored as secrets. Reveal is audited from the Secrets screen."
        >
          <ul className="divide-y divide-border rounded-md border border-border">
            {['NODE_ENV=production', 'PORT=' + (app.internalPort || 8080), 'LOG_LEVEL=info'].map((row) => (
              <li key={row} className="px-3 py-2 font-mono text-xs text-foreground">
                {row}
              </li>
            ))}
          </ul>
          {app.domain && (
            <p className="mt-3 text-sm text-muted-foreground">
              Public domain <span className="font-mono text-foreground">{app.domain}</span> is routed to this app.
            </p>
          )}
        </Panel>
      )}

      {tab === 'danger' && (
        <Panel title="Danger zone" className="border-failed/30">
          <div className="flex flex-col divide-y divide-border">
            <DangerRow
              title="Stop application"
              body="Scales all replicas to zero. Traffic will be refused until restarted."
              action={
                <Button variant="outline" size="sm" onClick={() => setDanger('stop')} disabled={stopped}>
                  Stop
                </Button>
              }
            />
            <DangerRow
              title="Delete application"
              body="Permanently removes the application, its config, and deployment history. Volumes are not deleted."
              action={
                <Button variant="destructive" size="sm" onClick={() => setDanger('delete')}>
                  <Trash2 className="size-3.5" /> Delete
                </Button>
              }
            />
          </div>
        </Panel>
      )}

      {/* Confirmations */}
      <ConfirmDialog
        open={danger === 'stop'}
        onOpenChange={(o) => !o && setDanger(null)}
        tone="warn"
        title={`Stop ${app.name}?`}
        description="All replicas will be scaled to zero. Requests will fail until the app is started again."
        confirmLabel="Stop application"
        onConfirm={() => setDanger(null)}
      />
      <ConfirmDialog
        open={danger === 'rollback'}
        onOpenChange={(o) => !o && setDanger(null)}
        tone="warn"
        title="Roll back"
        description={
          rollbackTarget ? (
            <div className="flex flex-col gap-2">
              <p>This replaces the current release. Confirm the target — the wrong SHA at 3am is a real outage.</p>
              <div className="grid grid-cols-2 gap-2 rounded-md border border-border bg-secondary/40 p-2 font-mono text-xs">
                <div>
                  <p className="text-[10px] uppercase tracking-wide text-muted-foreground">Current</p>
                  <p className="text-foreground">{app.currentSha ?? '—'}</p>
                </div>
                <div>
                  <p className="text-[10px] uppercase tracking-wide text-degraded">Roll back to</p>
                  <p className="text-foreground">{rollbackTarget.sha}</p>
                  <p className="mt-1 text-muted-foreground">{rollbackTarget.message}</p>
                  <p className="text-muted-foreground">
                    {rollbackTarget.branch} · {rollbackTarget.author}
                  </p>
                </div>
              </div>
            </div>
          ) : (
            'Select a deployment first.'
          )
        }
        confirmLabel={`Roll back to ${rollbackTarget?.sha ?? ''}`}
        requireTyped={rollbackTarget?.sha}
        onConfirm={() => setDanger(null)}
      />
      <ConfirmDialog
        open={danger === 'delete'}
        onOpenChange={(o) => !o && setDanger(null)}
        tone="danger"
        title={`Delete ${app.name}`}
        description="This permanently removes the application and its deployment history. Volumes are not deleted."
        requireTyped={app.name}
        confirmLabel="Delete application"
        onConfirm={() => setDanger(null)}
      />
    </div>
  )
}

function DangerRow({ title, body, action }: { title: string; body: string; action: React.ReactNode }) {
  return (
    <div className="flex items-start justify-between gap-4 py-4 first:pt-0 last:pb-0">
      <div className="min-w-0">
        <p className="text-sm font-medium text-foreground">{title}</p>
        <p className="text-xs text-muted-foreground">{body}</p>
      </div>
      <div className="shrink-0">{action}</div>
    </div>
  )
}
