'use client'

import { useCallback, useEffect, useState } from 'react'
import { Eye, KeyRound, Loader2, RotateCcw, ShieldOff } from 'lucide-react'

import { ConfirmDialog } from '@/components/confirm-dialog'
import { ProblemBanner } from '@/components/problem-banner'
import { Button } from '@/components/ui/button'
import { CopyField } from '@/components/ui/copy-field'
import { Panel } from '@/components/ui/panel'
import { client } from '@/lib/api/client'
import type { components } from '@/lib/api/schema'
import { formatRelative } from '@/lib/status'
import { cn } from '@/lib/utils'

type Detail = components['schemas']['DatabaseDetailDto']
type Credential = components['schemas']['CredentialDto']

/** The port each engine listens on inside its own container. */
const ENGINE_PORT: Record<string, number> = {
  postgres: 5432,
  mysql: 3306,
  mongodb: 27017,
  redis: 6379,
}

const SCHEME: Record<string, string> = {
  postgres: 'postgresql',
  mysql: 'mysql',
  mongodb: 'mongodb',
  redis: 'redis',
}

/**
 * The credentials for a database, and how to connect with them.
 *
 * Without this a database could be provisioned and never used: the detail screen
 * fetched the database and offered to delete it, and nothing anywhere returned
 * the username or the password. The engine ran perfectly and nothing could
 * connect to it.
 */
export function CredentialsPanel({ db }: { db: Detail }) {
  const id = db.summary.id
  const [credentials, setCredentials] = useState<Credential[] | null>(null)
  const [revealed, setRevealed] = useState<Record<string, string>>({})
  const [error, setError] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)
  const [confirmRotate, setConfirmRotate] = useState(false)
  const [revokeTarget, setRevokeTarget] = useState<Credential | null>(null)

  const load = useCallback(async () => {
    try {
      const res = await client.GET('/api/v1/databases/{id}/credentials', { params: { path: { id } } })
      setCredentials(res.data ?? [])
      setError(null)
    } catch (err) {
      setError(err)
      setCredentials([])
    }
  }, [id])

  useEffect(() => {
    void load()
  }, [load])

  /** Audited on the server, and deliberately not cached anywhere. */
  async function reveal(credential: Credential) {
    setError(null)
    try {
      const res = await client.POST('/api/v1/databases/{id}/credentials/{credentialId}/reveal', {
        params: { path: { id, credentialId: credential.id } },
      })

      if (res.data?.value) {
        setRevealed((prev) => ({ ...prev, [credential.id]: res.data!.value }))
      }
    } catch (err) {
      setError(err)
    }
  }

  async function rotate() {
    setBusy(true)
    setError(null)
    try {
      await client.POST('/api/v1/databases/{id}/credentials/rotate', { params: { path: { id } } })

      // The old password stops working the moment this returns, so anything
      // still holding it is now broken. Clearing revealed values makes that
      // obvious rather than leaving a stale secret on screen.
      setRevealed({})
      setConfirmRotate(false)
      await load()
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  async function revoke(credential: Credential) {
    setBusy(true)
    setError(null)
    try {
      await client.POST('/api/v1/databases/{id}/credentials/{credentialId}/revoke', {
        params: { path: { id, credentialId: credential.id } },
      })
      setRevokeTarget(null)
      await load()
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  const engine = db.summary.engine
  const port = ENGINE_PORT[engine] ?? 0
  const primary = credentials?.find((c) => c.isPrimary && c.state === 'active') ?? null
  const password = primary ? revealed[primary.id] : undefined

  /**
   * Built against the container name, which is the address that works.
   *
   * Workloads reach each other over their shared network by container name, so
   * this is what an attached application uses. It resolves nowhere else — from
   * the host or a laptop the database is not reachable at all unless a port was
   * published, which is off by default.
   */
  const connection =
    primary && port
      ? `${SCHEME[engine] ?? engine}://${primary.username ?? 'app'}:${password ?? '<password>'}@${db.summary.slug}:${port}` +
        (db.databaseName ? `/${db.databaseName}` : '')
      : null

  return (
    <Panel
      title="Credentials"
      description="Revealing a password requires an elevated permission and writes an audit entry."
      actions={
        <Button variant="outline" size="sm" disabled={busy} onClick={() => setConfirmRotate(true)}>
          <RotateCcw className="size-3.5" /> Rotate
        </Button>
      }
    >
      {error != null && <ProblemBanner error={error} />}

      {credentials === null ? (
        <p className="flex items-center gap-2 text-sm text-muted-foreground">
          <Loader2 className="size-4 animate-spin text-transitional" />
          Loading credentials…
        </p>
      ) : credentials.length === 0 ? (
        <p className="text-sm text-muted-foreground">No credentials recorded for this database.</p>
      ) : (
        <div className="flex flex-col gap-4">
          <ul className="divide-y divide-border rounded-md border border-border">
            {credentials.map((c) => {
              const revoked = c.state !== 'active'
              return (
                <li key={c.id} className="flex flex-wrap items-center gap-3 px-3 py-2">
                  <KeyRound className={cn('size-3.5 shrink-0', revoked ? 'text-muted-foreground' : 'text-running')} />
                  <span className="font-mono text-xs text-foreground">{c.username ?? '—'}</span>

                  {c.isPrimary && (
                    <span className="rounded bg-running/15 px-1.5 py-0.5 text-[11px] font-medium text-running">
                      primary
                    </span>
                  )}
                  {revoked && (
                    <span className="rounded bg-secondary px-1.5 py-0.5 font-mono text-[11px] text-muted-foreground">
                      {c.state}
                    </span>
                  )}

                  <span className="min-w-0 flex-1 truncate font-mono text-xs text-muted-foreground">
                    {revealed[c.id] ?? c.password.value}
                  </span>

                  <span className="font-mono text-[11px] text-muted-foreground">{formatRelative(c.createdAt)}</span>

                  {!revoked && !revealed[c.id] && (
                    <button
                      type="button"
                      onClick={() => reveal(c)}
                      className="inline-flex shrink-0 items-center gap-1 text-xs text-muted-foreground hover:text-foreground"
                      title="Reveal — this is written to the audit log"
                    >
                      <Eye className="size-3.5" /> Reveal
                    </button>
                  )}

                  {!revoked && !c.isPrimary && (
                    <button
                      type="button"
                      onClick={() => setRevokeTarget(c)}
                      className="inline-flex shrink-0 items-center gap-1 text-xs text-muted-foreground hover:text-failed"
                    >
                      <ShieldOff className="size-3.5" /> Revoke
                    </button>
                  )}
                </li>
              )
            })}
          </ul>

          {connection && (
            <div>
              <p className="mb-1.5 text-xs text-muted-foreground">
                Connection string. The host is the container name — it resolves from an application attached to this
                database, and from nowhere else.
                {!password && ' Reveal the password to fill it in.'}
              </p>
              <CopyField value={connection} />
              {db.publishedPort ? (
                <p className="mt-2 text-xs text-muted-foreground">
                  Also published on the host at{' '}
                  <span className="font-mono text-foreground">
                    {db.publishBindAddress ?? '127.0.0.1'}:{String(db.publishedPort)}
                  </span>
                  .
                </p>
              ) : (
                <p className="mt-2 text-xs text-muted-foreground">
                  Not published to the host, which is the default. Reach it through an attached application, or over an
                  SSH tunnel.
                </p>
              )}
            </div>
          )}
        </div>
      )}

      <ConfirmDialog
        open={confirmRotate}
        onOpenChange={(o) => !o && setConfirmRotate(false)}
        tone="warn"
        title={`Rotate credentials for ${db.summary.slug}?`}
        description="A new password is generated and the old one stops working immediately. Anything still using it — an attached application, a script, a saved connection — breaks until it picks up the new value."
        confirmLabel={busy ? 'Rotating…' : 'Rotate credentials'}
        onConfirm={rotate}
      />

      <ConfirmDialog
        open={revokeTarget !== null}
        onOpenChange={(o) => !o && setRevokeTarget(null)}
        tone="danger"
        title={`Revoke ${revokeTarget?.username ?? 'credential'}?`}
        description="This account stops being able to connect. Anything using it fails immediately."
        confirmLabel={busy ? 'Revoking…' : 'Revoke'}
        onConfirm={() => revokeTarget && revoke(revokeTarget)}
      />
    </Panel>
  )
}
