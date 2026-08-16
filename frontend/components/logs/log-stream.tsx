'use client'

import { useEffect, useRef, useState } from 'react'
import { Pause, Play, Search } from 'lucide-react'

import { cn } from '@/lib/utils'

type Line = { timestamp: string; stream: 'stdout' | 'stderr'; text: string }

/**
 * A database's container log, live.
 *
 * Only databases have one. The API exposes /api/v1/databases/{id}/logs/stream
 * and nothing equivalent for applications, so this component takes a database
 * id rather than a workload of any kind — a name it could not honour.
 *
 * What this replaced generated plausible lines on a timer: a payments API
 * serving charges, retrying webhooks against hooks.acme.io, and reporting slow
 * queries against a ledger table. It looked exactly like a real log, which is
 * what made it dangerous on a screen an operator reads to decide something.
 */
export function LogStream({
  databaseId,
  height = '24rem',
}: {
  databaseId: string
  height?: string
}) {
  const [lines, setLines] = useState<Line[]>([])
  const [filter, setFilter] = useState('')
  const [paused, setPaused] = useState(false)
  const [closed, setClosed] = useState<string | null>(null)

  const box = useRef<HTMLDivElement>(null)
  const pausedRef = useRef(paused)
  pausedRef.current = paused

  useEffect(() => {
    setLines([])
    setClosed(null)

    const source = new EventSource(`/api/v1/databases/${databaseId}/logs/stream`, { withCredentials: true })

    source.addEventListener('log.line', (ev: MessageEvent<string>) => {
      if (pausedRef.current) return

      try {
        const line = JSON.parse(ev.data) as Line

        // Bounded. A container that logs steadily would otherwise grow this
        // array until the tab dies, and nobody scrolls back ten thousand lines.
        setLines((prev) => (prev.length > 2000 ? [...prev.slice(-1500), line] : [...prev, line]))
      } catch {
        /* ignore malformed frames */
      }
    })

    source.addEventListener('stream.closing', (ev: MessageEvent<string>) => {
      try {
        const { reason } = JSON.parse(ev.data) as { reason?: string }

        // The server closes deliberately when a container floods the stream.
        // Saying so beats a log that simply stops with no explanation.
        setClosed(reason ?? 'closed')
      } catch {
        setClosed('closed')
      }
    })

    source.onerror = () => setClosed((prev) => prev ?? 'disconnected')

    return () => source.close()
  }, [databaseId])

  useEffect(() => {
    if (!paused && box.current) {
      box.current.scrollTop = box.current.scrollHeight
    }
  }, [lines, paused])

  const shown = filter ? lines.filter((l) => l.text.toLowerCase().includes(filter.toLowerCase())) : lines

  return (
    <div className="flex flex-col">
      <div className="flex items-center gap-2 border-b border-border px-3 py-2">
        <span className="relative flex items-center">
          <Search className="pointer-events-none absolute left-2 size-3.5 text-muted-foreground" />
          <input
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder="Filter…"
            aria-label="Filter log lines"
            className="h-7 w-40 rounded border border-input bg-background pl-7 pr-2 font-mono text-xs text-foreground placeholder:text-muted-foreground focus-visible:border-ring focus-visible:outline-none"
          />
        </span>

        <button
          type="button"
          onClick={() => setPaused((p) => !p)}
          className="inline-flex items-center gap-1 rounded border border-border px-2 py-1 font-mono text-xs text-muted-foreground hover:text-foreground"
        >
          {paused ? <Play className="size-3" /> : <Pause className="size-3" />}
          {paused ? 'Resume' : 'Pause'}
        </button>

        <span className="flex-1" />
        <span className="font-mono text-[11px] text-muted-foreground">
          {shown.length} line{shown.length === 1 ? '' : 's'}
          {closed ? ` · ${closed}` : ''}
        </span>
      </div>

      <div ref={box} className="overflow-auto bg-background/60 px-3 py-2 font-mono text-xs" style={{ height }}>
        {shown.length === 0 ? (
          <p className="text-muted-foreground">{closed ? `Stream ${closed}.` : 'Waiting for output…'}</p>
        ) : (
          shown.map((l, i) => (
            <p key={`${l.timestamp}-${i}`} className="whitespace-pre-wrap break-all">
              <span className="text-muted-foreground/60">{l.timestamp.slice(11, 23)} </span>
              <span className={cn(l.stream === 'stderr' ? 'text-failed' : 'text-foreground')}>{l.text}</span>
            </p>
          ))
        )}
      </div>
    </div>
  )
}
