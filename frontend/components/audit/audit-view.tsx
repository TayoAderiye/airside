'use client'

import { useMemo, useState } from 'react'
import { CheckCircle2, ShieldX, XCircle } from 'lucide-react'

import { Field, NativeSelect, TextInput } from '@/components/ui/field'
import { PageHeader, Panel } from '@/components/ui/panel'
import { auditEntries } from '@/lib/api/mock'
import type { AuditEntry } from '@/lib/api/types'
import { formatRelative } from '@/lib/status'
import { cn } from '@/lib/utils'

const RESULT: Record<AuditEntry['result'], { icon: typeof CheckCircle2; text: string; label: string }> = {
  success: { icon: CheckCircle2, text: 'text-running', label: 'success' },
  denied: { icon: ShieldX, text: 'text-degraded', label: 'denied' },
  failed: { icon: XCircle, text: 'text-failed', label: 'failed' },
}

export function AuditView() {
  const [query, setQuery] = useState('')
  const [result, setResult] = useState<'all' | AuditEntry['result']>('all')
  const [actor, setActor] = useState('all')

  const actors = useMemo(() => Array.from(new Set(auditEntries.map((e) => e.user))).sort(), [])

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    return [...auditEntries]
      .sort((a, b) => +new Date(b.timestamp) - +new Date(a.timestamp))
      .filter((e) => {
        if (result !== 'all' && e.result !== result) return false
        if (actor !== 'all' && e.user !== actor) return false
        if (!q) return true
        const hay = `${e.action} ${e.resource} ${e.user} ${e.ip} ${JSON.stringify(e.metadata ?? {})}`.toLowerCase()
        return hay.includes(q)
      })
  }, [query, result, actor])

  return (
    <div className="flex flex-col gap-5">
      <PageHeader
        title="Audit Logs"
        description="Immutable record of privileged actions. Denied and failed actions are highlighted."
      />

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
          <NativeSelect
            id="audit-result"
            value={result}
            onChange={(e) => setResult(e.target.value as typeof result)}
          >
            <option value="all">All results</option>
            <option value="success">Success</option>
            <option value="denied">Denied</option>
            <option value="failed">Failed</option>
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
        {filtered.length === 0 ? (
          <p className="px-4 py-10 text-center text-sm text-muted-foreground">No entries match these filters.</p>
        ) : (
          <ul className="divide-y divide-border">
            {filtered.map((e) => {
              const r = RESULT[e.result]
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
                  <span className="font-mono text-xs text-muted-foreground">{formatRelative(e.timestamp)}</span>
                  <div className="min-w-0">
                    <span className="font-mono text-sm text-foreground">{e.action}</span>
                    <span className="text-muted-foreground"> on </span>
                    <span className="font-mono text-sm text-foreground">{e.resource}</span>
                    {e.metadata && (
                      <div className="mt-0.5 flex flex-wrap gap-1.5">
                        {Object.entries(e.metadata).map(([k, v]) => (
                          <span key={k} className="rounded bg-secondary px-1.5 py-0.5 font-mono text-[11px] text-muted-foreground">
                            {k}={v}
                          </span>
                        ))}
                      </div>
                    )}
                  </div>
                  <span className="font-mono text-xs text-muted-foreground">
                    {e.user}
                    <span className="block text-[11px] text-muted-foreground/60">{e.ip}</span>
                  </span>
                  <span className={cn('inline-flex items-center gap-1.5 text-xs font-medium', r.text)}>
                    <Icon className="size-3.5" />
                    {r.label}
                  </span>
                </li>
              )
            })}
          </ul>
        )}
      </Panel>
    </div>
  )
}
