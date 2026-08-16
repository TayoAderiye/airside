'use client'

import { useCallback, useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import Link from 'next/link'
import {
  RotateCcw,
  Square,
  Play,
  Trash2,
  ArrowUpRight,
  GitCommitHorizontal,
  Loader2,
  Eye,
  Database as DatabaseIcon,
  Unlink,
} from 'lucide-react'

import { PageHeader, Panel, StatItem } from '@/components/ui/panel'
import { StatusBadge } from '@/components/status-badge'
import { AppSourceGlyph } from '@/components/app-source'
import { Tabs } from '@/components/ui/tabs'
import { NativeSelect } from '@/components/ui/field'
import { CopyField } from '@/components/ui/copy-field'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { ProblemBanner } from '@/components/problem-banner'
import { BackLink } from '@/components/ui/back-link'
import { Button } from '@/components/ui/button'
import { client } from '@/lib/api/client'
import { apiState, bytesToGiB, nanosToCores } from '@/lib/api/units'
import type { components } from '@/lib/api/schema'
import { formatRelative } from '@/lib/status'

type App = components['schemas']['ApplicationSummaryDto']
type Deployment = components['schemas']['DeploymentDto']
type EnvEntry = components['schemas']['EnvironmentEntryDto']
type Domain = components['schemas']['DomainDto']
type Attachment = components['schemas']['AttachmentDto']
type Database = components['schemas']['DatabaseSummaryDto']

type Danger = 'stop' | 'delete' | 'rollback' | null

export function AppDetailView({ applicationId }: { applicationId: string }) {
  const router = useRouter()

  const [app, setApp] = useState<App | null>(null)
  const [deployments, setDeployments] = useState<Deployment[]>([])
  const [env, setEnv] = useState<EnvEntry[]>([])
  const [domains, setDomains] = useState<Domain[]>([])
  const [attachments, setAttachments] = useState<Attachment[]>([])
  const [databases, setDatabases] = useState<Database[]>([])
  const [attachTarget, setAttachTarget] = useState('')
  const [detachTarget, setDetachTarget] = useState<Attachment | null>(null)
  const [error, setError] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)

  const [tab, setTab] = useState('overview')
  const [danger, setDanger] = useState<Danger>(null)
  const [rollbackTarget, setRollbackTarget] = useState<Deployment | null>(null)

  const load = useCallback(async () => {
    try {
      const [appRes, deployRes, envRes, domainRes, attachRes, dbRes] = await Promise.all([
        client.GET('/api/v1/applications/{id}', { params: { path: { id: applicationId } } }),
        client.GET('/api/v1/applications/{id}/deployments', { params: { path: { id: applicationId } } }),
        client.GET('/api/v1/applications/{id}/environment', { params: { path: { id: applicationId } } }),
        client.GET('/api/v1/applications/{id}/domains', { params: { path: { id: applicationId } } }),
        client.GET('/api/v1/applications/{id}/databases', { params: { path: { id: applicationId } } }),
        client.GET('/api/v1/databases'),
      ])

      setApp(appRes.data ?? null)
      setDeployments(deployRes.data?.items ?? [])
      setEnv(envRes.data ?? [])
      setDomains(domainRes.data ?? [])
      setAttachments(attachRes.data ?? [])

      // Airside's own store is never attachable: its id is synthesised, and an
      // application reaching the control plane's database is not something to
      // offer as a menu option.
      setDatabases((dbRes.data?.items ?? []).filter((d) => !d.isSystem))
      setError(null)
    } catch (err) {
      setError(err)
    }
  }, [applicationId])

  useEffect(() => {
    void load()
  }, [load])

  /** Every lifecycle call returns a job; the view reloads once it is accepted. */
  async function lifecycle(action: 'start' | 'stop' | 'restart') {
    setBusy(true)
    setError(null)
    try {
      // Spelled out rather than built by interpolation and cast. A template
      // literal here type-checks against whichever route the cast names, so a
      // path that does not exist would compile and fail at runtime.
      const params = { path: { id: applicationId } }

      if (action === 'start') {
        await client.POST('/api/v1/applications/{id}/start', { params })
      } else if (action === 'stop') {
        await client.POST('/api/v1/applications/{id}/stop', { params })
      } else {
        await client.POST('/api/v1/applications/{id}/restart', { params })
      }

      await load()
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
      setDanger(null)
    }
  }

  async function rollback() {
    if (!rollbackTarget) return
    setBusy(true)
    setError(null)
    try {
      const res = await client.POST('/api/v1/deployments/{id}/rollback', {
        params: { path: { id: rollbackTarget.id } },
      })
      setDanger(null)
      if (res.data?.jobId) {
        router.push(`/applications/new/deploying?job=${res.data.jobId}&app=${applicationId}`)
      }
    } catch (err) {
      setError(err)
      setBusy(false)
    }
  }

  async function remove(confirmSlug: string, deleteVolumes: boolean) {
    setBusy(true)
    setError(null)
    try {
      await client.POST('/api/v1/applications/{id}/delete', {
        params: { path: { id: applicationId } },
        body: { confirmSlug, deleteVolumes },
      })
      router.replace('/applications')
    } catch (err) {
      setError(err)
      setBusy(false)
      setDanger(null)
    }
  }

  /**
   * Attaching is what puts the two containers on a shared network.
   *
   * It is not a label: until this call, the application has no route to the
   * database at all, and the connection details are injected as environment
   * variables rather than copied by hand. Isolation is pairwise, so an
   * application reaches exactly the databases listed here and nothing else.
   */
  async function attach() {
    if (!attachTarget) return
    setBusy(true)
    setError(null)
    try {
      await client.POST('/api/v1/applications/{id}/databases', {
        params: { path: { id: applicationId } },
        body: { databaseId: attachTarget, envKeyPrefix: null },
      })
      setAttachTarget('')
      await load()
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  async function detach(attachment: Attachment) {
    setBusy(true)
    setError(null)
    try {
      await client.DELETE('/api/v1/applications/{id}/databases/{attachmentId}', {
        params: { path: { id: applicationId, attachmentId: attachment.id } },
      })
      setDetachTarget(null)
      await load()
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  /** Audited on the server; the value replaces the mask in place. */
  async function reveal(key: string) {
    try {
      const res = await client.POST('/api/v1/applications/{id}/environment/{key}/reveal', {
        params: { path: { id: applicationId, key } },
      })
      const value = res.data?.value
      if (typeof value === 'string') {
        setEnv((prev) => prev.map((e) => (e.key === key ? { ...e, value, isSecret: false } : e)))
      }
    } catch (err) {
      setError(err)
    }
  }

  if (error != null && !app) {
    return (
      <div className="flex flex-col gap-4">
        <BackLink href="/applications">Applications</BackLink>
        <ProblemBanner error={error} />
      </div>
    )
  }

  if (!app) {
    return (
      <div className="flex items-center gap-2 text-sm text-muted-foreground">
        <Loader2 className="size-4 animate-spin text-transitional" />
        Loading application…
      </div>
    )
  }

  const state = apiState(app.state)
  const stopped = state === 'Stopped'
  const current = deployments.find((d) => d.isCurrent) ?? null
  const primaryDomain = domains[0]?.hostname ?? null

  const tabs = [
    { id: 'overview', label: 'Overview' },
    { id: 'deployments', label: 'Deployments', badge: deployments.length },
    { id: 'databases', label: 'Databases', badge: attachments.length },
    { id: 'env', label: 'Environment', badge: env.length },
    { id: 'danger', label: 'Danger zone' },
  ]

  const attachable = databases.filter((d) => !attachments.some((a) => a.databaseId === d.id))

  return (
    <div className="flex flex-col gap-5">
      <BackLink href="/applications">Applications</BackLink>

      <PageHeader
        title={
          <span className="flex items-center gap-3">
            <AppSourceGlyph source={app.sourceKind as never} className="size-9" />
            {app.displayName || app.slug}
          </span>
        }
        description={`${app.sourceKind} · port ${app.containerPort}`}
        actions={
          <div className="flex items-center gap-2">
            <StatusBadge state={state} />
            {stopped ? (
              <Button variant="outline" size="sm" disabled={busy} onClick={() => lifecycle('start')}>
                <Play className="size-3.5" /> Start
              </Button>
            ) : (
              <Button variant="outline" size="sm" disabled={busy} onClick={() => setDanger('stop')}>
                <Square className="size-3.5" /> Stop
              </Button>
            )}
          </div>
        }
      />

      {error != null && <ProblemBanner error={error} />}

      <Tabs tabs={tabs} active={tab} onChange={setTab} />

      {tab === 'overview' && (
        <div className="grid grid-cols-1 gap-5 lg:grid-cols-3">
          <Panel title="Reserved" className="lg:col-span-2" description="The limits promised to this workload.">
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <StatItem label="CPU" value={`${nanosToCores(app.cpuNanos).toFixed(2)} cores`} mono />
              <StatItem label="Memory" value={`${bytesToGiB(app.memoryBytes).toFixed(1)} GiB`} mono />
            </div>
          </Panel>

          <Panel title="Networking">
            <div className="flex flex-col gap-3">
              <StatItem label="Container port" value={app.containerPort} mono />
              <StatItem label="Domain" value={primaryDomain ?? 'none'} mono />
              <StatItem label="Current release" value={current ? `#${current.number}` : '—'} mono />
            </div>
            {primaryDomain && (
              <a
                href={`https://${primaryDomain}`}
                className="mt-3 inline-flex items-center gap-1 font-mono text-xs text-accent hover:underline"
              >
                Open {primaryDomain}
                <ArrowUpRight className="size-3" />
              </a>
            )}
          </Panel>

          <Panel title="Internal address" className="lg:col-span-3">
            <CopyField value={`${app.slug}:${app.containerPort}`} />
          </Panel>
        </div>
      )}

      {tab === 'deployments' && (
        <Panel title="Deployment history" bodyClassName="p-0">
          {deployments.length === 0 ? (
            <p className="p-4 text-sm text-muted-foreground">No deployments yet.</p>
          ) : (
            <ul className="divide-y divide-border">
              {deployments.map((d) => (
                <li key={d.id} className="flex flex-col gap-3 p-4 sm:flex-row sm:items-center sm:gap-4">
                  <div className="flex min-w-0 flex-1 items-start gap-3">
                    <GitCommitHorizontal className="mt-0.5 size-4 shrink-0 text-muted-foreground" />
                    <div className="min-w-0">
                      <div className="flex items-center gap-2">
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
                        {d.durationMs ? ` · ${Math.round(Number(d.durationMs) / 1000)}s` : ''}
                      </p>
                      {d.errorMessage && <p className="mt-1 text-xs text-failed">{d.errorMessage}</p>}
                    </div>
                  </div>
                  <div className="flex shrink-0 items-center gap-3 pl-7 sm:pl-0">
                    <StatusBadge state={apiState(d.status)} />
                    {!d.isCurrent && d.status !== 'failed' && (
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
          )}
        </Panel>
      )}

      {tab === 'databases' && (
        <Panel
          title="Attached databases"
          description="Attaching joins the two containers on a shared network and injects the connection as environment variables. Until then this application has no route to the database at all."
        >
          {attachments.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              Nothing attached. This application cannot reach any database.
            </p>
          ) : (
            <ul className="mb-4 divide-y divide-border rounded-md border border-border">
              {attachments.map((a) => (
                <li key={a.id} className="flex flex-col gap-2 px-3 py-2.5">
                  <div className="flex flex-wrap items-center gap-3">
                    <DatabaseIcon className="size-3.5 shrink-0 text-running" />
                    <Link href={`/databases/${a.databaseId}`} className="font-mono text-xs text-foreground hover:text-accent">
                      {a.databaseSlug}
                    </Link>
                    <span className="rounded bg-secondary px-1.5 py-0.5 font-mono text-[11px] text-muted-foreground">
                      {a.engine}
                    </span>
                    <span className="font-mono text-[11px] text-muted-foreground">
                      attached {formatRelative(a.attachedAt)}
                    </span>
                    <span className="flex-1" />
                    <button
                      type="button"
                      onClick={() => setDetachTarget(a)}
                      className="inline-flex shrink-0 items-center gap-1 text-xs text-muted-foreground hover:text-failed"
                    >
                      <Unlink className="size-3.5" /> Detach
                    </button>
                  </div>

                  {a.injectedKeys.length > 0 && (
                    <p className="flex flex-wrap gap-1.5">
                      {/* The point of attaching: the application reads these
                          rather than carrying a connection string someone
                          pasted, so a rotation reaches it without an edit. */}
                      {a.injectedKeys.map((k) => (
                        <span
                          key={k}
                          className="rounded bg-secondary px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground"
                        >
                          {k}
                        </span>
                      ))}
                    </p>
                  )}
                </li>
              ))}
            </ul>
          )}

          {attachable.length > 0 ? (
            <div className="flex flex-wrap items-end gap-2">
              <label className="min-w-48 flex-1">
                <span className="mb-1 block text-xs text-muted-foreground">Attach a database</span>
                <NativeSelect value={attachTarget} onChange={(e) => setAttachTarget(e.target.value)}>
                  <option value="">Choose…</option>
                  {attachable.map((d) => (
                    <option key={d.id} value={d.id}>
                      {d.displayName || d.slug} · {d.engine} {d.version}
                    </option>
                  ))}
                </NativeSelect>
              </label>
              <Button variant="outline" size="sm" disabled={!attachTarget || busy} onClick={attach}>
                {busy ? 'Attaching…' : 'Attach'}
              </Button>
            </div>
          ) : (
            <p className="text-sm text-muted-foreground">
              {databases.length === 0
                ? 'No databases on this host yet.'
                : 'Every database on this host is already attached.'}
            </p>
          )}
        </Panel>
      )}

      {tab === 'env' && (
        <Panel
          title="Environment variables"
          description="Injected at container start. Revealing a secret is a separate, audited action."
        >
          {env.length === 0 ? (
            <p className="text-sm text-muted-foreground">None set.</p>
          ) : (
            <ul className="divide-y divide-border rounded-md border border-border">
              {env.map((e) => (
                <li key={e.key} className="flex items-center gap-3 px-3 py-2">
                  <span className="font-mono text-xs text-muted-foreground">{e.key}</span>
                  <span className="min-w-0 flex-1 truncate font-mono text-xs text-foreground">{e.value}</span>
                  {e.source !== 'manual' && (
                    <span className="rounded bg-secondary px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground">
                      {e.source}
                    </span>
                  )}
                  {e.isSecret && e.revealUrl && (
                    <button
                      type="button"
                      onClick={() => reveal(e.key)}
                      className="inline-flex shrink-0 items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
                      title="Reveal — this is written to the audit log"
                    >
                      <Eye className="size-3.5" /> Reveal
                    </button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </Panel>
      )}

      {tab === 'danger' && (
        <Panel title="Danger zone" className="border-failed/30">
          <div className="flex flex-col divide-y divide-border">
            <DangerRow
              title="Stop application"
              body="Stops the container. Traffic will be refused until it is started again."
              action={
                <Button variant="outline" size="sm" onClick={() => setDanger('stop')} disabled={stopped || busy}>
                  Stop
                </Button>
              }
            />
            <DangerRow
              title="Delete application"
              body="Removes the application and its deployment history. Volumes are kept unless you say otherwise."
              action={
                <Button variant="destructive" size="sm" onClick={() => setDanger('delete')} disabled={busy}>
                  <Trash2 className="size-3.5" /> Delete
                </Button>
              }
            />
          </div>
        </Panel>
      )}

      <ConfirmDialog
        open={danger === 'stop'}
        onOpenChange={(o) => !o && setDanger(null)}
        tone="warn"
        title={`Stop ${app.slug}?`}
        description="The container stops and requests will fail until it is started again."
        confirmLabel="Stop application"
        onConfirm={() => lifecycle('stop')}
      />

      <ConfirmDialog
        open={danger === 'rollback'}
        onOpenChange={(o) => !o && setDanger(null)}
        tone="warn"
        title="Roll back"
        description={
          rollbackTarget ? (
            <div className="flex flex-col gap-2">
              <p>This replaces the current release. Confirm the target — the wrong release at 3am is a real outage.</p>
              <div className="grid grid-cols-2 gap-2 rounded-md border border-border bg-secondary/40 p-2 font-mono text-xs">
                <div>
                  <p className="text-[10px] uppercase tracking-wide text-muted-foreground">Current</p>
                  <p className="text-foreground">{current ? `#${current.number}` : '—'}</p>
                </div>
                <div>
                  <p className="text-[10px] uppercase tracking-wide text-degraded">Roll back to</p>
                  <p className="text-foreground">#{rollbackTarget.number}</p>
                  {rollbackTarget.commitMessage && (
                    <p className="mt-1 text-muted-foreground">{rollbackTarget.commitMessage}</p>
                  )}
                </div>
              </div>
            </div>
          ) : (
            'Select a deployment first.'
          )
        }
        confirmLabel={`Roll back to #${rollbackTarget?.number ?? ''}`}
        requireTyped={rollbackTarget ? String(rollbackTarget.number) : undefined}
        onConfirm={rollback}
      />

      <ConfirmDialog
        open={detachTarget !== null}
        onOpenChange={(o) => !o && setDetachTarget(null)}
        tone="warn"
        title={`Detach ${detachTarget?.databaseSlug ?? ''}?`}
        description={
          detachTarget ? (
            <div className="flex flex-col gap-2">
              <p>
                The two containers leave their shared network and the injected variables stop being set. This
                application loses its route to{' '}
                <span className="font-mono text-foreground">{detachTarget.databaseSlug}</span> immediately — anything
                mid-query fails.
              </p>
              <p className="text-muted-foreground">The database itself is untouched, and nothing in it is deleted.</p>
            </div>
          ) : null
        }
        confirmLabel={busy ? 'Detaching…' : 'Detach'}
        onConfirm={() => detachTarget && detach(detachTarget)}
      />

      <ConfirmDialog
        open={danger === 'delete'}
        onOpenChange={(o) => !o && setDanger(null)}
        tone="danger"
        title={`Delete ${app.slug}`}
        description="This removes the application and its deployment history. Any domains attached to it must be dealt with first — the API will say so if they are."
        confirmLabel="Delete application"
        requireTyped={app.slug}
        onConfirm={() => remove(app.slug, false)}
      />
    </div>
  )
}

function DangerRow({ title, body, action }: { title: string; body: string; action: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-3 py-4 first:pt-0 last:pb-0 sm:flex-row sm:items-center sm:justify-between">
      <div className="min-w-0">
        <p className="text-sm font-medium text-foreground">{title}</p>
        <p className="text-sm text-muted-foreground">{body}</p>
      </div>
      <div className="shrink-0">{action}</div>
    </div>
  )
}
