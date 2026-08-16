'use client'

import { useMemo, useState } from 'react'
import { Play, ShieldAlert } from 'lucide-react'

import { ConfirmDialog } from '@/components/confirm-dialog'
import { ProblemBanner } from '@/components/problem-banner'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/field'
import { Panel } from '@/components/ui/panel'
import { client } from '@/lib/api/client'
import { ApiError } from '@/lib/api/problem'
import type { components } from '@/lib/api/schema'
import { cn } from '@/lib/utils'

type Db = components['schemas']['DatabaseSummaryDto']
type Result = components['schemas']['QueryResponseDto']

const STARTERS: Record<string, string> = {
  postgres: 'SELECT 1;',
  mysql: 'SELECT 1;',
  mongodb: 'db.stats()',
  redis: 'SCAN 0 COUNT 20',
}

export function QueryConsole({
  db,
  initialStatement,
}: {
  db: Db

  /**
   * Seeded by the schema browser when a table is picked. The component is
   * remounted rather than updated so an in-progress statement is never replaced
   * under the cursor.
   */
  initialStatement?: string
}) {
  const [statement, setStatement] = useState(initialStatement ?? STARTERS[db.engine] ?? '')
  const [running, setRunning] = useState(false)
  const [result, setResult] = useState<Result | null>(null)
  const [error, setError] = useState<unknown>(null)
  const [confirmWrite, setConfirmWrite] = useState(false)

  const looksWrite = useMemo(
    () => /\b(insert|update|delete|drop|alter|flush|set|del)\b/i.test(statement),
    [statement],
  )
  const stopped = db.state === 'stopped' || db.state === 'provisioning' || db.state === 'deleting'

  async function run() {
    setRunning(true)
    setError(null)
    try {
      const res = await client.POST('/api/v1/databases/{id}/query', {
        params: { path: { id: db.id } },
        body: { statement, maxRows: 200, timeoutSeconds: 15 },
      })
      setResult(res.data ?? null)
    } catch (err) {
      setResult(null)
      setError(err)
    } finally {
      setRunning(false)
    }
  }

  const policy = error instanceof ApiError && (error.code === 'query.command_blocked' || error.code === 'query.command_requires_elevation')

  return (
    <div className="flex flex-col gap-4">
      <Panel
        title="Query"
        description="Reads need database.query. Destructive Redis commands need database.query_destructive. Refusals are policy, not syntax."
        bodyClassName="p-0"
      >
        <div className="border-b border-border px-3 py-2">
          <Textarea
            value={statement}
            onChange={(e) => setStatement(e.target.value)}
            onKeyDown={(e) => {
              if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') {
                e.preventDefault()
                if (looksWrite) setConfirmWrite(true)
                else void run()
              }
            }}
            spellCheck={false}
            rows={8}
            aria-label="Query"
            className="min-h-[5.5rem] resize-y border-0 bg-transparent px-1 font-mono text-xs leading-5"
          />
        </div>
        <div className="flex flex-wrap items-center gap-2 px-3 py-2">
          <Button
            size="sm"
            disabled={stopped || running || !statement.trim()}
            onClick={() => (looksWrite ? setConfirmWrite(true) : void run())}
          >
            <Play className="size-3.5" />
            {running ? 'Running' : 'Run'}
          </Button>
          <span className="font-mono text-[11px] text-muted-foreground">⌘↩</span>
          {looksWrite && (
            <span className="inline-flex items-center gap-1.5 text-xs text-degraded">
              <ShieldAlert className="size-3.5" />
              Looks like a write
            </span>
          )}
        </div>
      </Panel>

      {error != null && (
        <div>
          {policy && <p className="mb-1 font-mono text-[11px] text-degraded">Policy refusal</p>}
          <ProblemBanner error={error} />
        </div>
      )}

      {result && (
        <Panel
          title="Result"
          description={`${result.durationMs}ms · ${result.rowsAffected} affected${result.truncated ? ' · truncated' : ''}`}
          bodyClassName="p-0"
        >
          {result.columns.length === 0 ? (
            <p className="px-4 py-6 text-sm text-muted-foreground">No result set.</p>
          ) : (
            <div className="overflow-auto">
              <table className="min-w-full text-left font-mono text-xs">
                <thead>
                  <tr className="border-b border-border text-[11px] uppercase text-muted-foreground">
                    {result.columns.map((c) => (
                      <th key={c} className="px-3 py-2">
                        {c}
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {result.rows.map((row, i) => (
                    <tr key={i} className="border-b border-border/70">
                      {row.map((cell, j) => (
                        <td key={j} className={cn('max-w-xs truncate px-3 py-1.5', cell == null && 'text-muted-foreground')}>
                          {cell == null ? 'null' : String(cell)}
                        </td>
                      ))}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Panel>
      )}

      <ConfirmDialog
        open={confirmWrite}
        onOpenChange={setConfirmWrite}
        tone="danger"
        title={`Run write on ${db.slug}?`}
        confirmLabel="Run write"
        description={
          <pre className="max-h-32 overflow-auto rounded-md border border-border bg-secondary/50 p-2 font-mono text-xs whitespace-pre-wrap">
            {statement.trim()}
          </pre>
        }
        onConfirm={() => void run()}
      />
    </div>
  )
}
