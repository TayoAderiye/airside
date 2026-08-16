import {
  Circle,
  LoaderCircle,
  Octagon,
  Square,
  Triangle,
  type LucideIcon,
} from 'lucide-react'

import type { StatusKind, WorkloadState } from '@/lib/api/types'
import { KIND_CLASSES, statusMeta } from '@/lib/status'
import { cn } from '@/lib/utils'

/**
 * Every status carries three redundant signals so it survives colour-blindness,
 * peripheral vision, and 3am fatigue: a distinct icon SHAPE, a colour, and a
 * text LABEL. Transitional states pulse so "working" never looks like "stuck".
 */
const ICON_BY_KIND: Record<StatusKind, LucideIcon> = {
  running: Circle,
  degraded: Triangle,
  failed: Octagon,
  stopped: Square,
  transitional: LoaderCircle,
}

export function StatusDot({
  state,
  className,
}: {
  state: WorkloadState
  className?: string
}) {
  const meta = statusMeta(state)
  const Icon = ICON_BY_KIND[meta.kind]
  const c = KIND_CLASSES[meta.kind]
  return (
    <Icon
      aria-hidden
      className={cn(
        'size-3',
        c.text,
        meta.kind === 'running' && 'fill-current',
        meta.kind === 'transitional' && 'animate-spin',
        className,
      )}
    />
  )
}

export function StatusBadge({
  state,
  className,
}: {
  state: WorkloadState
  className?: string
}) {
  const meta = statusMeta(state)
  const Icon = ICON_BY_KIND[meta.kind]
  const c = KIND_CLASSES[meta.kind]
  return (
    <span
      role="status"
      className={cn(
        'inline-flex items-center gap-1.5 rounded-md border px-2 py-0.5 font-mono text-xs font-medium tracking-tight',
        c.bg,
        c.border,
        c.text,
        className,
      )}
    >
      <Icon
        aria-hidden
        className={cn(
          'size-3',
          meta.kind === 'running' && 'fill-current',
          meta.kind === 'transitional' && 'animate-spin',
          meta.transitional && 'animate-status-pulse',
        )}
      />
      {meta.label}
    </span>
  )
}
