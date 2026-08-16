'use client'

import { useState } from 'react'
import { CloudUpload, FileArchive, HardDriveDownload, Lock, ShieldCheck } from 'lucide-react'

import { ConfirmDialog } from '@/components/confirm-dialog'
import { StatusDot } from '@/components/status-badge'
import { Button } from '@/components/ui/button'
import { PageHeader, Panel, StatItem } from '@/components/ui/panel'
import { backups, snapshots } from '@/lib/api/mock'
import type { BackupPolicy, BackupSnapshot } from '@/lib/api/types'
import { formatRelative } from '@/lib/status'
import { cn } from '@/lib/utils'

const RESULT: Record<string, { state: 'Running' | 'Failed' | 'Stopped'; label: string }> = {
  success: { state: 'Running', label: 'Last run succeeded' },
  running: { state: 'Running', label: 'Running now' },
  failed: { state: 'Failed', label: 'Last run failed' },
  never: { state: 'Stopped', label: 'Never run' },
}

export function BackupsView() {
  const [restore, setRestore] = useState<{ policy: BackupPolicy; snap: BackupSnapshot } | null>(null)

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title="Backups"
        description="Scheduled backup policies for databases on this host."
        actions={<Button variant="default">New policy</Button>}
      />

      <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
        {backups.map((b) => {
          const result = RESULT[b.lastResult]
          const running = b.lastResult === 'running'
          const latest = snapshots.find((s) => s.policyId === b.id && s.status === 'success')
          return (
            <Panel key={b.id} className={cn(b.lastResult === 'failed' && 'border-failed/40')}>
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0">
                  <div className="flex items-center gap-2">
                    <h2 className="truncate font-display text-sm font-semibold text-foreground">{b.resourceName}</h2>
                    <span className="rounded bg-secondary px-1.5 py-0.5 text-[11px] text-muted-foreground">
                      {b.resourceType}
                    </span>
                  </div>
                  <p className="mt-0.5 font-mono text-xs text-muted-foreground">{b.schedule}</p>
                </div>
                <span className="inline-flex shrink-0 items-center gap-1.5 text-xs text-muted-foreground">
                  <StatusDot state={result.state} />
                  {running ? 'running' : b.lastRunAt ? formatRelative(b.lastRunAt) : 'never'}
                </span>
              </div>

              <div className="mt-4 flex flex-col gap-2 border-t border-border pt-3">
                <StatItem
                  label="Destination"
                  value={
                    <span className="inline-flex items-center gap-1.5">
                      {b.destination === 's3' ? <CloudUpload className="size-3.5" /> : <HardDriveDownload className="size-3.5" />}
                      {b.destination === 's3' ? b.s3Bucket : 'local volume'}
                    </span>
                  }
                  mono
                />
                <StatItem label="Retention" value={b.retentionDays > 0 ? `${b.retentionDays} days` : 'no retention'} mono />
                <div className="flex items-center gap-2 pt-1">
                  <Flag on={b.compression} icon={FileArchive} label="Compression" />
                  <Flag on={b.encryption} icon={b.encryption ? Lock : ShieldCheck} label="Encryption" warnIfOff />
                </div>
              </div>

              <div className="mt-4 flex gap-2">
                <Button variant="outline" size="sm" disabled={running}>
                  Run now
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  disabled={!latest}
                  onClick={() => latest && setRestore({ policy: b, snap: latest })}
                >
                  Restore
                </Button>
                <Button variant="ghost" size="sm">
                  Edit
                </Button>
              </div>
            </Panel>
          )
        })}
      </div>

      {restore && (
        <RestoreDialog
          policy={restore.policy}
          snap={restore.snap}
          open
          onOpenChange={(open) => !open && setRestore(null)}
        />
      )}
    </div>
  )
}

function RestoreDialog({
  policy,
  snap,
  open,
  onOpenChange,
}: {
  policy: BackupPolicy
  snap: BackupSnapshot
  open: boolean
  onOpenChange: (open: boolean) => void
}) {
  const isRedis = snap.engine === 'redis'
  const when = new Date(snap.createdAt).toISOString().replace('T', ' ').slice(0, 19)

  return (
    <ConfirmDialog
      open={open}
      onOpenChange={onOpenChange}
      tone="danger"
      title={`Restore ${policy.resourceName}`}
      confirmLabel="Restore"
      requireTyped={policy.resourceName}
      extraConfirms={[
        {
          id: 'snapshot',
          label: 'Take a pre-restore snapshot',
          description: 'Captures the current data first so this restore can be undone.',
          defaultChecked: true,
        },
      ]}
      description={
        <div className="flex flex-col gap-2">
          <p>
            Replaces the live data on <span className="font-mono text-foreground">{policy.resourceName}</span> with
            snapshot <span className="font-mono text-foreground">{snap.id}</span> from {when} UTC ({snap.sizeGiB} GiB,{' '}
            {snap.destination}).
          </p>
          {isRedis && (
            <p className="rounded-md border border-degraded/40 bg-degraded-soft px-2.5 py-2 text-degraded">
              Redis must stop for restore. Connected clients will drop and stay down until the instance starts again.
            </p>
          )}
        </div>
      }
    />
  )
}

function Flag({
  on,
  icon: Icon,
  label,
  warnIfOff,
}: {
  on: boolean
  icon: React.ComponentType<{ className?: string }>
  label: string
  warnIfOff?: boolean
}) {
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1 rounded px-1.5 py-0.5 text-[11px]',
        on ? 'bg-running/15 text-running' : warnIfOff ? 'bg-degraded/15 text-degraded' : 'bg-secondary text-muted-foreground',
      )}
    >
      <Icon className="size-3" />
      {label} {on ? 'on' : 'off'}
    </span>
  )
}
