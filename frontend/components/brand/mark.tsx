import { cn } from '@/lib/utils'
import { PRODUCT_NAME } from '@/lib/brand'

/** Wordmark. The live pip is the only ornament — this is an on-call tool, not a brand site. */
export function BrandMark({
  size = 'sm',
  className,
}: {
  size?: 'sm' | 'lg'
  className?: string
}) {
  return (
    <span className={cn('inline-flex items-center gap-2', className)}>
      <span
        className={cn(
          'relative grid place-items-center rounded-[3px] border border-primary/50 bg-primary/10',
          size === 'lg' ? 'size-6' : 'size-5',
        )}
        aria-hidden
      >
        <span className="size-1.5 rounded-full bg-running" />
      </span>
      <span
        className={cn(
          'font-display font-semibold tracking-tight text-foreground',
          size === 'lg' ? 'text-lg' : 'text-sm',
        )}
      >
        {PRODUCT_NAME}
      </span>
    </span>
  )
}
