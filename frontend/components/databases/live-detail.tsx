'use client'

import { useEffect, useState } from 'react'

import { QueryConsole } from '@/components/databases/query-console'
import { JobWatcher } from '@/components/job-watcher'
import { ProblemBanner } from '@/components/problem-banner'
import { StatusBadge } from '@/components/status-badge'
import { WarningsList } from '@/components/warnings-list'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { Button } from '@/components/ui/button'
import { Panel } from '@/components/ui/panel'
import { client } from '@/lib/api/client'
import { ApiError } from '@/lib/api/problem'
import type { JobAccepted } from '@/lib/api/jobs'
import type { components } from '@/lib/api/schema'
import { apiState, bytesToGiB, nanosToCores } from '@/lib/api/units'

type Detail = components['schemas']['DatabaseDetailDto']

export function LiveDatabaseDetail({ id }: { id: string }) {
  const [db, setDb] = useState<Detail | null>(null)
  const [error, setError] = useState<unknown>(null)
  const [job, setJob] = useState<JobAccepted | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [expected, setExpected] = useState<string>()

  async function load() {
    const res = await client.GET('/api/v1/databases/{id}', { params: { path: { id } } })
    setDb(res.data ?? null)
  }

  useEffect(() => {
    load().catch(setError)
  }, [id])

  async function lifecycle(path: '/api/v1/databases/{id}/start' | '/api/v1/databases/{id}/stop' | '/api/v1/databases/{id}/restart') {
    setError(null)
    try {
      const res = await client.POST(path, { params: { path: { id } } })
      if (res.data) setJob(res.data)
    } catch (err) {
      setError(err)
    }
  }

  async function remove(confirmSlug: string) {
    setError(null)
    try {
      const res = await client.POST('/api/v1/databases/{id}/delete', {
        params: { path: { id } },
        body: { confirmSlug, deleteVolume: false },
      })
      if (res.data) setJob(res.data)
    } catch (err) {
      if (err instanceof ApiError && err.expected) {
        setExpected(err.expected)
        setConfirmDelete(true)
        return
      }
      setError(err)
    }
  }

  if (!db) {
    return error != null ? <ProblemBanner error={error} /> : <p className="text-sm text-muted-foreground">Loading…</p>
  }

  const summary = db.summary

  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div>
          <div className="flex items-center gap-3">
            <h1 className="font-display text-2xl font-semibold">{summary.displayName || summary.slug}</h1>
            <StatusBadge state={apiState(summary.state)} />
          </div>
          <p className="font-mono text-sm text-muted-foreground">
            {summary.engine} {summary.version} · {nanosToCores(summary.cpuNanos).toFixed(2)} cores ·{' '}
            {bytesToGiB(summary.memoryBytes).toFixed(1)} GiB
          </p>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button variant="outline" size="sm" onClick={() => void lifecycle('/api/v1/databases/{id}/start')}>
            Start
          </Button>
          <Button variant="outline" size="sm" onClick={() => void lifecycle('/api/v1/databases/{id}/stop')}>
            Stop
          </Button>
          <Button variant="outline" size="sm" onClick={() => void lifecycle('/api/v1/databases/{id}/restart')}>
            Restart
          </Button>
          <Button variant="destructive" size="sm" onClick={() => void remove(summary.slug)}>
            Delete
          </Button>
        </div>
      </div>

      {error != null && <ProblemBanner error={error} />}
      <WarningsList warnings={db.warnings} />
      {job && <JobWatcher job={job} onDone={() => void load()} />}

      <Panel title="Query">
        <QueryConsole db={summary} />
      </Panel>

      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        tone="danger"
        title={`Delete ${summary.slug}?`}
        confirmLabel="Delete database"
        requireTyped={expected ?? summary.slug}
        description="Type the slug. Volume deletion is a separate field on the request — this call keeps the volume."
        extraConfirms={[
          {
            id: 'destroyVolume',
            label: 'Also delete the data volume',
            description: 'Destroys the data. Off by default.',
            danger: true,
          },
        ]}
        onConfirm={(extras) => {
          void client.POST('/api/v1/databases/{id}/delete', {
            params: { path: { id } },
            body: { confirmSlug: expected ?? summary.slug, deleteVolume: extras.destroyVolume },
          })
        }}
      />
    </div>
  )
}
