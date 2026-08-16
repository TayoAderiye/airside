import type { DatabaseEngine } from '@/lib/api/types'
import { cn } from '@/lib/utils'

/**
 * Engine identity. Each engine gets a stable monogram + accent so operators
 * recognise it by shape/colour before reading the label. Not brand logos —
 * these are neutral monograms so we don't imply official marks.
 */
const ENGINE: Record<DatabaseEngine, { label: string; mono: string; className: string }> = {
  postgres: { label: 'PostgreSQL', mono: 'Pg', className: 'bg-[#3b6ea5]/15 text-[#7fb3e8] border-[#3b6ea5]/40' },
  mysql: { label: 'MySQL', mono: 'My', className: 'bg-[#c9820b]/15 text-[#e0a94b] border-[#c9820b]/40' },
  mongodb: { label: 'MongoDB', mono: 'Mo', className: 'bg-running/15 text-running border-running/40' },
  redis: { label: 'Redis', mono: 'Rd', className: 'bg-failed/15 text-[#f08a84] border-failed/40' },
}

export function engineLabel(engine: DatabaseEngine) {
  return ENGINE[engine].label
}

export function EngineGlyph({ engine, className }: { engine: DatabaseEngine; className?: string }) {
  const e = ENGINE[engine]
  return (
    <span
      className={cn(
        'inline-flex size-8 shrink-0 items-center justify-center rounded-md border font-mono text-xs font-semibold',
        e.className,
        className,
      )}
      aria-hidden
    >
      {e.mono}
    </span>
  )
}
