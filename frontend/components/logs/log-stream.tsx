'use client'

import { useEffect, useMemo, useRef, useState } from 'react'
import { ArrowDownToLine, Download, Pause, Play, Search } from 'lucide-react'
import type { LogLine, StreamState } from '@/lib/api/types'
import { subscribeLogs } from '@/lib/api/mock'
import { cn } from '@/lib/utils'

const MAX_LINES = 2000
const ROW_H = 20
const OVERSCAN = 12
const LEVELS = ['all', 'info', 'warn', 'error', 'debug'] as const
type LevelFilter = (typeof LEVELS)[number]
type Since = '15m' | '1h' | '6h' | 'all'

const LEVEL_STYLE: Record<LogLine['level'], string> = {
  info: 'text-muted-foreground',
  debug: 'text-accent-foreground/60',
  warn: 'text-degraded',
  error: 'text-failed',
}

/**
 * Streaming log viewer. Mirrors the logs SignalR hub in the assumed contract:
 * connection lifecycle, capped buffer, pause/resume, follow-tail, filters.
 * The list is windowed so a high-volume stream does not freeze the tab.
 */
export function LogStream({ source, height = '32rem' }: { source: string; height?: string }) {
  const [lines, setLines] = useState<LogLine[]>([])
  const [stream, setStream] = useState<StreamState>('connecting')
  const [paused, setPaused] = useState(false)
  const [follow, setFollow] = useState(true)
  const [level, setLevel] = useState<LevelFilter>('all')
  const [errorsOnly, setErrorsOnly] = useState(false)
  const [since, setSince] = useState<Since>('all')
  const [query, setQuery] = useState('')
  const [scrollTop, setScrollTop] = useState(0)
  const [viewportH, setViewportH] = useState(480)

  const pausedRef = useRef(paused)
  pausedRef.current = paused
  const scrollRef = useRef<HTMLDivElement>(null)
  const followRef = useRef(follow)
  followRef.current = follow

  useEffect(() => {
    const connectTimer = setTimeout(() => setStream('live'), 700)
    const unsub = subscribeLogs((line) => {
      if (pausedRef.current) return
      setLines((prev) => {
        const next = prev.length >= MAX_LINES ? prev.slice(prev.length - MAX_LINES + 1) : prev.slice()
        next.push(line)
        return next
      })
    })
    return () => {
      clearTimeout(connectTimer)
      unsub()
    }
  }, [source])

  useEffect(() => {
    if (follow && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight
    }
  }, [lines, follow])

  useEffect(() => {
    const el = scrollRef.current
    if (!el) return
    const ro = new ResizeObserver(() => setViewportH(el.clientHeight))
    ro.observe(el)
    setViewportH(el.clientHeight)
    return () => ro.disconnect()
  }, [])

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    const cutoff = sinceMs(since)
    const wantLevel = errorsOnly ? 'error' : level
    return lines.filter((l) => {
      if (wantLevel !== 'all' && l.level !== wantLevel) return false
      if (cutoff && new Date(l.ts).getTime() < cutoff) return false
      if (q && !l.message.toLowerCase().includes(q) && !l.source.toLowerCase().includes(q)) return false
      return true
    })
  }, [lines, level, query, errorsOnly, since])

  const start = Math.max(0, Math.floor(scrollTop / ROW_H) - OVERSCAN)
  const visible = Math.ceil(viewportH / ROW_H) + OVERSCAN * 2
  const end = Math.min(filtered.length, start + visible)
  const offsetY = start * ROW_H
  const totalH = Math.max(filtered.length * ROW_H, viewportH)

  function download() {
    const body = filtered
      .map((l) => `${l.ts} ${l.level.toUpperCase().padEnd(5)} ${l.source} ${l.message}`)
      .join('\n')
    const blob = new Blob([body], { type: 'text/plain' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `${source}-logs.txt`
    a.click()
    URL.revokeObjectURL(url)
  }

  return (
    <div className="flex flex-col" style={{ height }}>
      <div className="flex flex-wrap items-center gap-2 border-b border-border px-3 py-2">
        <StreamIndicator state={paused ? 'stalled' : stream} paused={paused} />

        <div className="relative ml-1 flex-1 sm:max-w-xs">
          <Search className="pointer-events-none absolute left-2 top-1/2 size-3.5 -translate-y-1/2 text-muted-foreground" />
          <input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Filter…"
            className="h-7 w-full rounded border border-input bg-background pl-7 pr-2 font-mono text-xs text-foreground placeholder:text-muted-foreground focus-visible:border-ring focus-visible:outline-none"
          />
        </div>

        <div className="flex rounded border border-input bg-secondary p-0.5">
          {LEVELS.map((lv) => (
            <button
              key={lv}
              type="button"
              onClick={() => {
                setLevel(lv)
                setErrorsOnly(false)
              }}
              aria-pressed={!errorsOnly && level === lv}
              disabled={errorsOnly}
              className={cn(
                'rounded px-2 py-0.5 font-mono text-[11px] capitalize transition-colors',
                !errorsOnly && level === lv ? 'bg-card text-foreground' : 'text-muted-foreground hover:text-foreground',
                errorsOnly && 'opacity-40',
              )}
            >
              {lv}
            </button>
          ))}
        </div>

        <button
          type="button"
          onClick={() => setErrorsOnly((v) => !v)}
          aria-pressed={errorsOnly}
          className={cn(
            'inline-flex h-7 items-center rounded border px-2 font-mono text-[11px]',
            errorsOnly ? 'border-failed/50 bg-failed-soft text-failed' : 'border-input text-muted-foreground hover:text-foreground',
          )}
        >
          Errors only
        </button>

        <select
          value={since}
          onChange={(e) => setSince(e.target.value as Since)}
          aria-label="Timestamp range"
          className="h-7 rounded border border-input bg-background px-2 font-mono text-[11px] text-foreground"
        >
          <option value="15m">Last 15m</option>
          <option value="1h">Last 1h</option>
          <option value="6h">Last 6h</option>
          <option value="all">All buffered</option>
        </select>

        <button
          type="button"
          onClick={() => setPaused((p) => !p)}
          className="inline-flex h-7 items-center gap-1 rounded border border-input px-2 text-xs text-foreground hover:bg-accent"
        >
          {paused ? <Play className="size-3.5" /> : <Pause className="size-3.5" />}
          {paused ? 'Resume' : 'Pause'}
        </button>
        <button
          type="button"
          onClick={() => setFollow((f) => !f)}
          aria-pressed={follow}
          className={cn(
            'inline-flex h-7 items-center gap-1 rounded border px-2 text-xs transition-colors',
            follow ? 'border-primary/50 bg-primary/10 text-primary' : 'border-input text-muted-foreground hover:bg-accent',
          )}
        >
          <ArrowDownToLine className="size-3.5" />
          Follow
        </button>
        <button
          type="button"
          onClick={download}
          className="inline-flex h-7 items-center gap-1 rounded border border-input px-2 text-xs text-foreground hover:bg-accent"
        >
          <Download className="size-3.5" />
          Download
        </button>
      </div>

      <div
        ref={scrollRef}
        onScroll={(e) => {
          const el = e.currentTarget
          setScrollTop(el.scrollTop)
          const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24
          if (!atBottom && followRef.current) setFollow(false)
        }}
        className="flex-1 overflow-auto bg-[#0a0d12] font-mono text-xs leading-5"
      >
        {filtered.length === 0 ? (
          <p className="py-8 text-center text-muted-foreground">
            {stream === 'connecting' ? 'Connecting to stream…' : 'No lines match the current filter.'}
          </p>
        ) : (
          <div style={{ height: totalH, position: 'relative' }}>
            <div style={{ transform: `translateY(${offsetY}px)` }} className="px-3 py-2">
              {filtered.slice(start, end).map((l) => (
                <div key={l.id} className="flex gap-3 whitespace-pre-wrap break-all" style={{ minHeight: ROW_H }}>
                  <span className="shrink-0 text-muted-foreground/70">{l.ts.slice(11, 23)}</span>
                  <span className={cn('w-10 shrink-0 uppercase', LEVEL_STYLE[l.level])}>{l.level}</span>
                  <span className="shrink-0 text-accent-foreground/50">{l.source}</span>
                  <span className="text-foreground/90">{l.message}</span>
                </div>
              ))}
            </div>
          </div>
        )}
      </div>

      <div className="flex items-center justify-between border-t border-border px-3 py-1.5 font-mono text-[11px] text-muted-foreground">
        <span>
          {filtered.length.toLocaleString()} shown · {lines.length.toLocaleString()} buffered
          {lines.length >= MAX_LINES && <span className="text-degraded"> · buffer full, oldest dropped</span>}
        </span>
        {!follow && <span className="text-degraded">paused following — scroll to bottom or hit Follow</span>}
      </div>
    </div>
  )
}

function sinceMs(since: Since): number | null {
  if (since === 'all') return null
  const map = { '15m': 15 * 60_000, '1h': 60 * 60_000, '6h': 6 * 60 * 60_000 }
  return Date.now() - map[since]
}

function StreamIndicator({ state, paused }: { state: StreamState; paused: boolean }) {
  const map: Record<StreamState, { label: string; dot: string; text: string }> = {
    connecting: { label: 'Connecting', dot: 'bg-transitional animate-status-pulse', text: 'text-transitional' },
    live: { label: 'Live', dot: 'bg-running animate-status-pulse', text: 'text-running' },
    stalled: { label: paused ? 'Paused' : 'Stalled', dot: 'bg-degraded', text: 'text-degraded' },
    reconnecting: { label: 'Reconnecting', dot: 'bg-transitional animate-status-pulse', text: 'text-transitional' },
    closed: { label: 'Closed', dot: 'bg-stopped', text: 'text-muted-foreground' },
    error: { label: 'Error', dot: 'bg-failed', text: 'text-failed' },
  }
  const s = map[state]
  return (
    <span className={cn('inline-flex items-center gap-1.5 font-mono text-xs font-medium', s.text)}>
      <span className={cn('size-2 rounded-full', s.dot)} />
      {s.label}
    </span>
  )
}
