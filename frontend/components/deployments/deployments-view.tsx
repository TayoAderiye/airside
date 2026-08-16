'use client'

import { useCallback, useEffect, useState } from 'react'
import Link from 'next/link'
import { useRouter } from 'next/navigation'
import { GitCommitHorizontal, Loader2, RotateCcw, Timer } from 'lucide-react'

import { ConfirmDialog } from '@/components/confirm-dialog'
import { ProblemBanner } from '@/components/problem-banner'
import { StatusBadge } from '@/components/status-badge'
import { Button } from '@/components/ui/button'
import { PageHeader, Panel } from '@/components/ui/panel'
import { client } from '@/lib/api/client'
import { apiState } from '@/lib/api/units'
import type { components } from '@/lib/api/schema'
import { formatRelative } from '@/lib/status'

type Deployment = components['schemas']['DeploymentDto']

/** A deployment plus the application it belongs to, which the DTO names only by id. */
type Row = { deployment: Deployment; appId: string; appName: string }

export function DeploymentsView() {
  const router = useRouter()
  const [rows, setRows] = useState<Row[] | null>(null)
  const [target, setTarget] = useState<Row | null>(null)
  const [error, setError] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      // Fanned out per application, because the API has no cross-application
      // deployment feed — deployments are only listed under the application
      // that owns them. Fine for one host with a handful of applications; if
      // that stops being true, this wants an endpoint rather than more requests.
      const appsRes = await client.GET('/api/v1/applications')
      const apps = appsRes.data?.items ?? []

      const perApp = await Promise.all(
        apps.map(async (a) => {
          const res = await client.GET('/api/v1/applications/{id}/deployments', {
            params: { path: { id: a.id } },
          })

          return (res.data?.items ?? []).map((d) => ({
            deployment: d,
            appId: a.id,
            appName: a.displayName || a.slug,
          }))
        }),
      )

      setRows(
        perApp
          .flat()
          .sort((x, y) => +new Date(y.deployment.startedAt) - +new Date(x.deployment.startedAt)),
      )
      setError(null)
    } catch (err) {
      setError(err)
      setRows([])
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  async function rollback() {
    if (!target) return
    setBusy(true)
    setError(null)
    try {
      const res = await client.POST('/api/v1/deployments/{id}/rollback', {
        params: { path: { id: target.deployment.id } },
      })
      const jobId = res.data?.jobId
      setTarget(null)
      if (jobId) {
        router.push(`/applications/new/deploying?job=${jobId}&app=${target.appId}`)
      }
    } catch (err) {
      setError(err)
      setBusy(false)
    }
  }

  const currentFor = (appId: string) =>
    rows?.find((r) => r.appId === appId && r.deployment.isCurrent)?.deployment ?? null

  return (
    <div className="flex flex-col gap-5">
      <PageHeader title="Deployments" description="Every rollout across all applications on this host, newest first." />

      {error != null && <ProblemBanner error={error} />}

      <Panel bodyClassName="p-0">
        {rows === null ? (
          <p className="flex items-center gap-2 p-4 text-sm text-muted-foreground">
            <Loader2 className="size-4 animate-spin text-transitional" />
            Loading deployments…
          </p>
        ) : rows.length === 0 ? (
          <p className="p-4 text-sm text-muted-foreground">
            No deployments yet. Deploy an application and its rollouts appear here.
          </p>
        ) : (
          <ul className="divide-y divide-border">
            {rows.map(({ deployment: d, appId, appName }) => (
              <li key={d.id} className="flex flex-col gap-3 p-4 sm:flex-row sm:items-center sm:gap-4">
                <div className="flex min-w-0 flex-1 items-start gap-3">
                  <GitCommitHorizontal className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
                  <div className="min-w-0">
                    <div className="flex flex-wrap items-center gap-2">
                      <Link
                        href={`/applications/${appId}`}
                        className="font-display text-sm font-semibold text-foreground hover:text-accent"
                      >
                        {appName}
                      </Link>
                      <span className="font-mono text-xs text-foreground">
                        #{d.number} {d.commitSha?.slice(0, 7) ?? d.imageRef ?? ''}
                      </span>
                      {d.branch && (
                        <span className="rounded bg-secondary px-1.5 py-0.5 font-mono text-[11px] text-muted-foreground">
                          {d.branch}
                        </span>
                      )}
                      {d.isCurrent && (
                        <span className="rounded bg-running/15 px-1.5 py-0.5 text-[11px] font-medium text-running">
                          current
                        </span>
                      )}
                    </div>
                    {d.commitMessage && <p className="mt-0.5 truncate text-sm text-foreground">{d.commitMessage}</p>}
                    <p className="font-mono text-xs text-muted-foreground">
                      {d.triggerKind} · {formatRelative(d.startedAt)}
                    </p>
                    {d.errorMessage && <p className="mt-1 text-xs text-failed">{d.errorMessage}</p>}
                  </div>
                </div>

                <div className="flex shrink-0 items-center gap-3 pl-7 sm:pl-0">
                  {d.durationMs != null && (
                    <span className="inline-flex items-center gap-1 font-mono text-xs text-muted-foreground">
                      <Timer className="size-3.5" />
                      {Math.round(Number(d.durationMs) / 1000)}s
                    </span>
                  )}
                  <StatusBadge state={apiState(d.status)} />
                  {!d.isCurrent && d.status !== 'failed' && (
                    <Button variant="outline" size="sm" onClick={() => setTarget({ deployment: d, appId, appName })}>
                      <RotateCcw className="size-3.5" /> Roll back
                    </Button>
                  )}
                </div>
              </li>
            ))}
          </ul>
        )}
      </Panel>

      <ConfirmDialog
        open={target !== null}
        onOpenChange={(o) => !o && setTarget(null)}
        tone="warn"
        title="Roll back"
        description={
          target ? (
            <div className="flex flex-col gap-2">
              <p>
                This replaces the current release of{' '}
                <span className="font-mono text-foreground">{target.appName}</span>. Confirm the target — the wrong
                release at 3am is a real outage.
              </p>
              <div className="grid grid-cols-2 gap-2 rounded-md border border-border bg-secondary/40 p-2 font-mono text-xs">
                <div>
                  <p className="text-[10px] uppercase tracking-wide text-muted-foreground">Current</p>
                  <p className="text-foreground">
                    {currentFor(target.appId) ? `#${currentFor(target.appId)!.number}` : '—'}
                  </p>
                </div>
                <div>
                  <p className="text-[10px] uppercase tracking-wide text-degraded">Roll back to</p>
                  <p className="text-foreground">#{target.deployment.number}</p>
                  {target.deployment.commitMessage && (
                    <p className="mt-1 text-muted-foreground">{target.deployment.commitMessage}</p>
                  )}
                </div>
              </div>
            </div>
          ) : null
        }
        confirmLabel={busy ? 'Rolling back…' : `Roll back to #${target?.deployment.number ?? ''}`}
        requireTyped={target ? String(target.deployment.number) : undefined}
        onConfirm={rollback}
      />
    </div>
  )
}
