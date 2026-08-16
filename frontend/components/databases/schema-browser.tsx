'use client'

import { useEffect, useState } from 'react'
import { ChevronDown, ChevronRight, KeyRound, Table2 } from 'lucide-react'

import { client } from '@/lib/api/client'
import { ApiError } from '@/lib/api/problem'
import type { components } from '@/lib/api/schema'
import { cn } from '@/lib/utils'

type Schema = components['schemas']['DatabaseSchemaDto']
type Table = components['schemas']['SchemaTableDto']

/**
 * The tables and columns of a database, beside the console that queries them.
 *
 * The console shipped as a bare text box: an operator had to already know the
 * table names to write anything, which on a database they did not create is a
 * guess. Introspection is per-engine and lives in the API, so this only renders
 * what it is given — including the engines that have nothing to render, which say
 * so rather than showing an empty list that reads as "no tables".
 *
 * Clicking a table writes a starter query rather than navigating, because the
 * point of the browser is to get someone typing against the right name.
 */
export function SchemaBrowser({
  databaseId,
  onSelectTable,
}: {
  databaseId: string
  onSelectTable: (table: Table) => void
}) {
  const [schema, setSchema] = useState<Schema | null>(null)
  const [unavailable, setUnavailable] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setSchema(null)
    setUnavailable(null)

    client
      .GET('/api/v1/databases/{id}/schema', { params: { path: { id: databaseId } } })
      .then((r) => {
        if (cancelled) return
        setSchema(r.data ?? null)

        // One table open on arrival when there is exactly one, which is the
        // common case on a database provisioned for a single application.
        const only = r.data?.tables
        if (only?.length === 1) setExpanded(keyOf(only[0]))
      })
      .catch((err) => {
        if (cancelled) return

        // Engines without tables answer with a reason. It is a refusal, not a
        // fault, so it is shown as prose rather than an error banner.
        setUnavailable(
          err instanceof ApiError && err.code === 'query.schema_unavailable'
            ? err.problem.detail
            : err instanceof ApiError
              ? err.problem.detail
              : 'The schema could not be read.',
        )
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })

    return () => {
      cancelled = true
    }
  }, [databaseId])

  if (loading) {
    return <p className="px-3 py-2 text-xs text-muted-foreground">Reading schema…</p>
  }

  if (unavailable) {
    return <p className="px-3 py-2 text-xs text-muted-foreground">{unavailable}</p>
  }

  const tables = schema?.tables ?? []

  if (tables.length === 0) {
    return <p className="px-3 py-2 text-xs text-muted-foreground">No tables yet.</p>
  }

  return (
    <ul className="flex flex-col">
      {tables.map((table) => {
        const id = keyOf(table)
        const open = expanded === id

        return (
          <li key={id}>
            <button
              type="button"
              onClick={() => setExpanded(open ? null : id)}
              className="flex w-full items-center gap-1.5 px-2 py-1.5 text-left hover:bg-secondary/60"
            >
              {open ? (
                <ChevronDown className="size-3 shrink-0 text-muted-foreground" />
              ) : (
                <ChevronRight className="size-3 shrink-0 text-muted-foreground" />
              )}
              <Table2 className="size-3 shrink-0 text-muted-foreground" />
              <span className="min-w-0 flex-1 truncate font-mono text-xs text-foreground">{table.name}</span>
              <span className="shrink-0 font-mono text-[10px] text-muted-foreground">
                {table.columns.length}
              </span>
            </button>

            {open && (
              <div className="border-l border-border pb-1 pl-5">
                <ul>
                  {table.columns.map((column) => (
                    <li
                      key={column.name}
                      className="flex items-baseline gap-1.5 py-0.5 pr-2 font-mono text-[11px]"
                    >
                      {column.isPrimaryKey && (
                        <KeyRound className="size-2.5 shrink-0 self-center text-degraded" />
                      )}
                      <span className={cn('truncate', column.isPrimaryKey ? 'text-foreground' : 'text-muted-foreground')}>
                        {column.name}
                      </span>
                      <span className="ml-auto shrink-0 text-muted-foreground/60">
                        {column.dataType}
                        {column.nullable ? '' : ' ·'}
                      </span>
                    </li>
                  ))}
                </ul>

                <button
                  type="button"
                  onClick={() => onSelectTable(table)}
                  className="mt-1 font-mono text-[11px] text-primary hover:underline"
                >
                  Query this table
                </button>
              </div>
            )}
          </li>
        )
      })}
    </ul>
  )
}

/** Qualified, because two schemas can hold a table of the same name. */
function keyOf(table: Table): string {
  return table.namespace ? `${table.namespace}.${table.name}` : table.name
}

/** The name to put in a statement, quoted only when it needs to be. */
export function qualify(table: Table): string {
  const quote = (part: string) => (/^[a-z_][a-z0-9_]*$/.test(part) ? part : `"${part}"`)

  return table.namespace && table.namespace !== 'public'
    ? `${quote(table.namespace)}.${quote(table.name)}`
    : quote(table.name)
}
