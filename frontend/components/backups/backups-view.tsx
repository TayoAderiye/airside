'use client'

import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import { CloudUpload, FileArchive, HardDriveDownload, Loader2, ShieldCheck } from 'lucide-react'

import { ConfirmDialog } from '@/components/confirm-dialog'
import { ProblemBanner } from '@/components/problem-banner'
import { StatusBadge } from '@/components/status-badge'
import { Button } from '@/components/ui/button'
import { EmptyState, PageHeader, Panel, StatItem } from '@/components/ui/panel'
import { client } from '@/lib/api/client'
import { apiState, bytesToGiB } from '@/lib/api/units'
import type { components } from '@/lib/api/schema'
import { formatRelative } from '@/lib/status'

type Database = components['schemas']['DatabaseSummaryDto']
type Backup = components['schemas']['BackupDto']

/**
 * Backups, arranged the way the API models them.
 *
 * There is no backup-policy entity. Settings — enabled, cron, retention — are
 * properties of a database, and the snapshots hang underneath it. The screen
 * this replaced invented a policy object with its own id, schedule and last
 * result, none of which the API has ever had.
 */
export function BackupsView() {
  const [databases, setDatabases] = useState<Database[] | null>(null)
  const [backups, setBackups] = useState<Record<string, Backup[]>>({})
  const [error, setError] = useState<unknown>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [restoreTarget, setRestoreTarget] = useState<{ backup: Backup; database: Database } | null>(null)

  const load = useCallback(async () => {
    try {
      const dbRes = await client.GET('/api/v1/databases')
      const list = dbRes.data?.items ?? []
      setDatabases(list)

      const pairs = await Promise.all(
        list.map(async (d) => {
          const res = await client.GET('/api/v1/databases/{id}/backups', { params: { path: { id: d.id } } })

          // A plain array, unlike deployments and audit which are cursor-paged.
          return [d.id, res.data ?? []] as const
        }),
      )

      setBackups(Object.fromEntries(pairs))
      setError(null)
    } catch (err) {
      setError(err)
      setDatabases([])
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  async function backUpNow(database: Database) {
    setBusy(database.id)
    setError(null)
    try {
      await client.POST('/api/v1/databases/{id}/backups', { params: { path: { id: database.id } } })
      await load()
    } catch (err) {
      setError(err)
    } finally {
      setBusy(null)
    }
  }

  async function systemBackup() {
    setBusy('system')
    setError(null)
    try {
      // The control-plane store and the Data Protection key ring, together in
      // one archive. Apart they are worth nothing — the key ring decrypts every
      // secret in the database.
      await client.POST('/api/v1/system/backups')
    } catch (err) {
      setError(err)
    } finally {
      setBusy(null)
    }
  }

  async function restore() {
    if (!restoreTarget) return
    const { backup, database } = restoreTarget

    setBusy(backup.id)
    setError(null)
    try {
      await client.POST('/api/v1/backups/{backupId}/restore', {
        params: { path: { backupId: backup.id } },
        body: { confirmSlug: database.slug },
      })
      setRestoreTarget(null)
      await load()
    } catch (err) {
      setError(err)
    } finally {
      setBusy(null)
    }
  }

  if (databases === null) {
    return (
      <p className="flex items-center gap-2 text-sm text-muted-foreground">
        <Loader2 className="size-4 animate-spin text-transitional" />
        Loading backups…
      </p>
    )
  }

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title="Backups"
        description="Database snapshots on this host, and the control-plane backup that makes them restorable."
        actions={
          <Button variant="outline" disabled={busy === 'system'} onClick={systemBackup}>
            <ShieldCheck className="size-4" />
            {busy === 'system' ? 'Starting…' : 'Back up the control plane'}
          </Button>
        }
      />

      {error != null && <ProblemBanner error={error} />}

      {databases.length === 0 ? (
        <EmptyState
          icon={FileArchive}
          title="No databases to back up"
          description="Provision a database and its snapshots appear here."
        />
      ) : (
        <div className="flex flex-col gap-4">
          {databases.map((d) => {
            const snapshots = backups[d.id] ?? []
            const latest = snapshots[0] ?? null

            return (
              <Panel
                key={d.id}
                title={
                  <Link href={`/databases/${d.id}`} className="hover:text-accent">
                    {d.displayName || d.slug}
                  </Link>
                }
                description={`${d.engine} ${d.version}`}
                actions={
                  <Button variant="outline" size="sm" disabled={busy === d.id} onClick={() => backUpNow(d)}>
                    <CloudUpload className="size-3.5" />
                    {busy === d.id ? 'Starting…' : 'Back up now'}
                  </Button>
                }
              >
                <div className="mb-3 grid grid-cols-2 gap-3 sm:grid-cols-4">
                  <StatItem label="Snapshots" value={snapshots.length} mono />
                  <StatItem label="Latest" value={latest ? formatRelative(latest.startedAt) : 'never'} mono />
                  <StatItem
                    label="Size"
                    value={latest?.sizeBytes ? `${bytesToGiB(latest.sizeBytes).toFixed(2)} GiB` : '—'}
                    mono
                  />
                  <StatItem label="Retained" value={snapshots.filter((s) => s.isRetained).length} mono />
                </div>

                {snapshots.length === 0 ? (
                  <p className="text-sm text-muted-foreground">No snapshots yet.</p>
                ) : (
                  <ul className="divide-y divide-border rounded-md border border-border">
                    {snapshots.slice(0, 5).map((b) => (
                      <li key={b.id} className="flex flex-wrap items-center gap-3 px-3 py-2">
                        <span className="font-mono text-xs text-muted-foreground">{formatRelative(b.startedAt)}</span>
                        <span className="rounded bg-secondary px-1.5 py-0.5 font-mono text-[11px] text-muted-foreground">
                          {b.triggerKind}
                        </span>
                        <span className="font-mono text-xs text-foreground">
                          {b.sizeBytes ? `${bytesToGiB(b.sizeBytes).toFixed(2)} GiB` : '—'}
                        </span>
                        <StatusBadge state={apiState(b.status)} />
                        {b.errorMessage && <span className="text-xs text-failed">{b.errorMessage}</span>}
                        <span className="flex-1" />
                        {b.status === 'succeeded' && (
                          <Button
                            variant="outline"
                            size="sm"
                            disabled={busy === b.id}
                            onClick={() => setRestoreTarget({ backup: b, database: d })}
                          >
                            <HardDriveDownload className="size-3.5" /> Restore
                          </Button>
                        )}
                      </li>
                    ))}
                  </ul>
                )}
              </Panel>
            )
          })}
        </div>
      )}

      <ConfirmDialog
        open={restoreTarget !== null}
        onOpenChange={(o) => !o && setRestoreTarget(null)}
        tone="danger"
        title={`Restore ${restoreTarget?.database.slug ?? ''}`}
        description={
          restoreTarget ? (
            <div className="flex flex-col gap-2">
              <p>
                This replaces everything currently in{' '}
                <span className="font-mono text-foreground">{restoreTarget.database.slug}</span> with the snapshot from{' '}
                {formatRelative(restoreTarget.backup.startedAt)}. Anything written since then is gone.
              </p>
              <p className="font-mono text-xs text-muted-foreground">
                {restoreTarget.backup.engineSnapshot}
                {restoreTarget.backup.sha256 ? ` · ${restoreTarget.backup.sha256.slice(0, 12)}` : ''}
              </p>
            </div>
          ) : null
        }
        confirmLabel={busy ? 'Restoring…' : 'Restore over the live database'}
        requireTyped={restoreTarget?.database.slug}
        onConfirm={restore}
      />
    </div>
  )
}
