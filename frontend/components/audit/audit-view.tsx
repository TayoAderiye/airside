'use client'

import { useCallback, useEffect, useMemo, useState } from 'react'
import { CheckCircle2, Loader2, ShieldX, XCircle } from 'lucide-react'

import { Button } from '@/components/ui/button'
import { Field, NativeSelect, TextInput } from '@/components/ui/field'
import { PageHeader, Panel } from '@/components/ui/panel'
import { ProblemBanner } from '@/components/problem-banner'
import { client } from '@/lib/api/client'
import type { components } from '@/lib/api/schema'
import { formatRelative } from '@/lib/status'
import { cn } from '@/lib/utils'

type AuditEvent = components['schemas']['AuditEventDto']

/**
 * The API lowercases its AuditResult enum, which is Success, Failure, Denied —
 * so the value is "failure". The mock used "failed", a value the API never
 * emits, so this lookup silently returned undefined for every failed action.
 */
const RESULT: Record<string, { icon: typeof CheckCircle2; text: string }> = {
  success: { icon: CheckCircle2, text: 'text-running' },
  denied: { icon: ShieldX, text: 'text-degraded' },
  failure: { icon: XCircle, text: 'text-failed' },
}

export function AuditView() {
  const [entries, setEntries] = useState<AuditEvent[] | null>(null)
  const [cursor, setCursor] = useState<string | null>(null)
  const [loadingMore, setLoadingMore] = useState(false)
  const [error, setError] = useState<unknown>(null)

  const [query, setQuery] = useState('')
  const [result, setResult] = useState<'all' | 'success' | 'denied' | 'failure'>('all')
  const [actor, setActor] = useState('all')

  const fetchPage = useCallback(async (after: string | null) => {
    // Keyset, not offset. This is an append-only log being read while it is
    // written to, and offset paging would skip rows as new ones arrive — which
    // in an audit log is the whole problem.
    const res = await client.GET('/api/v1/audit', {
      params: { query: after ? { cursor: after, limit: 100 } : { limit: 100 } },
    })

    return res.data
  }, [])

  useEffect(() => {
    let cancelled = false

    fetchPage(null)
      .then((page) => {
        if (cancelled) return
        setEntries(page?.items ?? [])
        setCursor(page?.nextCursor ?? null)
      })
      .catch((err) => {
        if (!cancelled) {
          setError(err)
          setEntries([])
        }
      })

    return () => {
      cancelled = true
    }
  }, [fetchPage])

  async function loadMore() {
    if (!cursor) return
    setLoadingMore(true)
    try {
      const page = await fetchPage(cursor)
      setEntries((prev) => [...(prev ?? []), ...(page?.items ?? [])])
      setCursor(page?.nextCursor ?? null)
    } catch (err) {
      setError(err)
    } finally {
      setLoadingMore(false)
    }
  }

  const actors = useMemo(
    () => Array.from(new Set((entries ?? []).map((e) => e.userEmail).filter(Boolean) as string[])).sort(),
    [entries],
  )

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()

    // Filtered over what has been loaded. The API filters by action and
    // resourceId server-side, but not by free text or actor, and pretending
    // otherwise would show "no matches" for entries simply not fetched yet.
    return (entries ?? []).filter((e) => {
      if (result !== 'all' && e.result !== result) return false
      if (actor !== 'all' && e.userEmail !== actor) return false
      if (!q) return true

      const hay = `${e.action} ${e.resourceKind ?? ''} ${e.resourceSlug ?? ''} ${e.userEmail ?? ''} ${e.ipAddress ?? ''}`
      return hay.toLowerCase().includes(q)
    })
  }, [entries, query, result, actor])

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title="Audit Logs"
        description="Immutable record of privileged actions. Denied and failed actions are highlighted."
      />

      {error != null && <ProblemBanner error={error} />}

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
        <Field label="Search" htmlFor="audit-q">
          <TextInput
            id="audit-q"
            mono
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="action, resource, IP…"
          />
        </Field>
        <Field label="Actor" htmlFor="audit-actor">
          <NativeSelect id="audit-actor" value={actor} onChange={(e) => setActor(e.target.value)}>
            <option value="all">All users</option>
            {actors.map((u) => (
              <option key={u} value={u}>
                {u}
              </option>
            ))}
          </NativeSelect>
        </Field>
        <Field label="Result" htmlFor="audit-result">
          <NativeSelect id="audit-result" value={result} onChange={(e) => setResult(e.target.value as typeof result)}>
            <option value="all">All results</option>
            <option value="success">Success</option>
            <option value="denied">Denied</option>
            <option value="failure">Failure</option>
          </NativeSelect>
        </Field>
      </div>

      <Panel bodyClassName="p-0">
        <div className="hidden grid-cols-[140px_1fr_180px_120px] gap-4 border-b border-border px-4 py-2 font-mono text-[11px] uppercase tracking-wide text-muted-foreground md:grid">
          <span>When</span>
          <span>Action</span>
          <span>Actor</span>
          <span>Result</span>
        </div>

        {entries === null ? (
          <p className="flex items-center justify-center gap-2 px-4 py-10 text-sm text-muted-foreground">
            <Loader2 className="size-4 animate-spin text-transitional" />
            Loading audit log…
          </p>
        ) : filtered.length === 0 ? (
          <p className="px-4 py-10 text-center text-sm text-muted-foreground">
            {entries.length === 0 ? 'Nothing has been audited yet.' : 'No entries match these filters.'}
          </p>
        ) : (
          <ul className="divide-y divide-border">
            {filtered.map((e) => {
              const r = RESULT[e.result] ?? { icon: XCircle, text: 'text-muted-foreground' }
              const Icon = r.icon
              const attention = e.result !== 'success'

              return (
                <li
                  key={e.id}
                  className={cn(
                    'grid grid-cols-1 gap-1 px-4 py-3 md:grid-cols-[140px_1fr_180px_120px] md:items-center md:gap-4',
                    attention && 'bg-card',
                  )}
                >
                  <span className="font-mono text-xs text-muted-foreground">{formatRelative(e.occurredAt)}</span>
                  <div className="min-w-0">
                    <span className="font-mono text-sm text-foreground">{e.action}</span>
                    {(e.resourceSlug || e.resourceKind) && (
                      <>
                        <span className="text-muted-foreground"> on </span>
                        <span className="font-mono text-sm text-foreground">
                          {e.resourceKind}
                          {e.resourceSlug ? `/${e.resourceSlug}` : ''}
                        </span>
                      </>
                    )}
                  </div>
                  <span className="font-mono text-xs text-muted-foreground">
                    {e.userEmail ?? 'system'}
                    <span className="block text-[11px] text-muted-foreground/60">{e.ipAddress ?? '—'}</span>
                  </span>
                  <span className={cn('inline-flex items-center gap-1.5 text-xs font-medium', r.text)}>
                    <Icon className="size-3.5" />
                    {e.result}
                  </span>
                </li>
              )
            })}
          </ul>
        )}

        {cursor && (
          <div className="border-t border-border p-3">
            <Button variant="outline" size="sm" className="w-full" disabled={loadingMore} onClick={loadMore}>
              {loadingMore ? 'Loading…' : 'Load older entries'}
            </Button>
          </div>
        )}
      </Panel>
    </div>
  )
}
