import type { ResourceTriple } from '@/lib/api/types'
import { cn } from '@/lib/utils'

/**
 * SIGNATURE ELEMENT — the allocation rail.
 *
 * The dashboard's hardest problem: Capacity, Allocated, and Used are routinely
 * confused, and confusing them causes incidents. A single progress bar cannot
 * say "90% allocated but 20% used". This rail shows all three on one track:
 *
 *   the full rail  = Capacity (what the host has)
 *   a bracket      = Allocated (the sum of promises) with a hard tick
 *   a solid fill   = Used (reality, right now)
 *
 * Over-commitment (bracket near the end) and over-consumption (fill near the
 * end) are two independent lengths, so both failure modes are visible at once.
 */

function pct(n: number, total: number) {
  if (total <= 0) return 0
  return Math.min(100, Math.max(0, (n / total) * 100))
}

function usedTone(ratio: number) {
  if (ratio >= 0.9) return { bar: 'bg-failed', text: 'text-failed' }
  if (ratio >= 0.75) return { bar: 'bg-degraded', text: 'text-degraded' }
  return { bar: 'bg-running', text: 'text-running' }
}

function allocTone(ratio: number) {
  if (ratio > 1) return { text: 'text-failed', tick: 'bg-failed' }
  if (ratio >= 0.9) return { text: 'text-degraded', tick: 'bg-degraded' }
  return { text: 'text-foreground', tick: 'bg-foreground/70' }
}

function fmt(n: number) {
  return Number.isInteger(n) ? String(n) : n.toFixed(1)
}

export function AllocationRail({
  label,
  triple,
  /** Optional live preview of a new allocation the operator is requesting. */
  requested,
  className,
}: {
  label: string
  triple: ResourceTriple
  requested?: number
  className?: string
}) {
  const { capacity, allocated, used, unit } = triple
  const usedPct = pct(used, capacity)
  const allocPct = pct(allocated, capacity)
  const usedRatio = capacity > 0 ? used / capacity : 0
  const allocRatio = capacity > 0 ? allocated / capacity : 0
  const ut = usedTone(usedRatio)
  const at = allocTone(allocRatio)

  const requestedPct = requested != null ? pct(requested, capacity) : null
  const overCommit = allocated > capacity

  return (
    <div className={cn('flex flex-col gap-2', className)}>
      <div className="flex items-baseline justify-between">
        <span className="text-sm font-medium text-foreground">{label}</span>
        <span className="font-mono text-xs text-muted-foreground">
          <span className={ut.text}>{fmt(used)}</span>
          {' / '}
          {fmt(capacity)} {unit}
        </span>
      </div>

      {/* The rail. Full width == capacity. */}
      <div
        className="relative h-6 w-full overflow-visible rounded-md border border-border bg-secondary"
        role="img"
        aria-label={`${label}: used ${fmt(used)} of ${fmt(capacity)} ${unit}, allocated ${fmt(allocated)} ${unit}`}
      >
        {/* Allocated region — diagonal hatch, no solid fill, so it reads as a
            promise that hasn't been consumed yet. Sits full-height behind Used. */}
        <div
          className={cn('absolute inset-y-0 left-0 rounded-l-md border-r-2', at.tick.replace('bg-', 'border-'))}
          style={{
            width: `${allocPct}%`,
            backgroundImage:
              'repeating-linear-gradient(-45deg, color-mix(in oklab, var(--foreground) 14%, transparent) 0 1.5px, transparent 1.5px 6px)',
          }}
        />

        {/* Used fill — solid reality, inset vertically so the allocated region
            stays visible as a band above and below it */}
        <div
          className={cn('absolute inset-y-1 left-0 rounded-sm transition-[width] duration-500', ut.bar)}
          style={{ width: `${usedPct}%` }}
        />

        {/* Requested preview marker (forms) */}
        {requestedPct != null && (
          <div
            className="absolute inset-y-[-3px] w-0.5 bg-primary"
            style={{ left: `calc(${requestedPct}% - 1px)` }}
            aria-hidden
          >
            <span className="absolute -top-4 left-1/2 -translate-x-1/2 rounded bg-primary px-1 font-mono text-[10px] leading-4 text-primary-foreground">
              req
            </span>
          </div>
        )}

        {/* Allocated hard tick label */}
        {allocPct > 2 && allocPct < 99 && (
          <div
            className={cn('absolute inset-y-[-3px] w-0.5', at.tick)}
            style={{ left: `calc(${allocPct}% - 1px)` }}
            aria-hidden
          />
        )}
      </div>

      {/* Legend — the three numbers, named and distinct */}
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 font-mono text-[11px]">
        <LegendItem swatch="border border-border bg-secondary" name="Capacity" value={`${fmt(capacity)} ${unit}`} />
        <LegendItem
          swatchStyle={{
            backgroundImage:
              'repeating-linear-gradient(-45deg, color-mix(in oklab, var(--foreground) 45%, transparent) 0 1.5px, transparent 1.5px 5px)',
          }}
          swatch={cn('border-r-2', at.tick.replace('bg-', 'border-'))}
          name="Allocated"
          value={`${fmt(allocated)} ${unit}`}
          badge={`${Math.round(allocRatio * 100)}%`}
          badgeClass={at.text}
        />
        <LegendItem
          swatch={ut.bar}
          name="Used"
          value={`${fmt(used)} ${unit}`}
          badge={`${Math.round(usedRatio * 100)}%`}
          badgeClass={ut.text}
        />
        {overCommit && (
          <span className="ml-auto rounded bg-failed-soft px-1.5 py-0.5 font-medium text-failed">
            Over-committed
          </span>
        )}
      </div>
    </div>
  )
}

function LegendItem({
  swatch,
  swatchStyle,
  name,
  value,
  badge,
  badgeClass,
}: {
  swatch: string
  swatchStyle?: React.CSSProperties
  name: string
  value: string
  badge?: string
  badgeClass?: string
}) {
  return (
    <span className="inline-flex items-center gap-1.5 text-muted-foreground">
      <span className={cn('inline-block h-3 w-3 rounded-sm', swatch)} style={swatchStyle} aria-hidden />
      {name}
      <span className="text-foreground">{value}</span>
      {badge && <span className={cn('font-semibold', badgeClass)}>{badge}</span>}
    </span>
  )
}
