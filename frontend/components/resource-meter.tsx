import { cn } from '@/lib/utils'

/**
 * Compact used/limit meter for list rows and detail headers. Colour escalates
 * with pressure (green -> amber -> red) and is always paired with the numeric
 * value, so it never depends on colour alone.
 */
function tone(ratio: number) {
  if (ratio >= 0.9) return 'bg-failed'
  if (ratio >= 0.75) return 'bg-degraded'
  return 'bg-running'
}

export function ResourceMeter({
  label,
  used,
  limit,
  unit,
  className,
}: {
  label: string
  used: number
  limit: number
  unit: string
  className?: string
}) {
  const ratio = limit > 0 ? Math.min(1, used / limit) : 0
  const fmt = (n: number) => (Number.isInteger(n) ? String(n) : n.toFixed(1))
  return (
    <div className={cn('flex min-w-0 flex-col gap-1', className)}>
      <div className="flex items-baseline justify-between gap-2">
        <span className="text-[11px] uppercase tracking-wide text-muted-foreground">{label}</span>
        <span className="font-mono text-xs text-foreground">
          {fmt(used)}
          <span className="text-muted-foreground">
            /{fmt(limit)} {unit}
          </span>
        </span>
      </div>
      <div className="h-1.5 w-full overflow-hidden rounded-full bg-secondary">
        <div
          className={cn('h-full rounded-full transition-[width] duration-500', tone(ratio))}
          style={{ width: `${ratio * 100}%` }}
        />
      </div>
    </div>
  )
}
