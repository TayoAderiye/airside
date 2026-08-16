import { cn } from '@/lib/utils'

/** Placeholder block. Never carries numbers or labels that look like live data. */
export function Skeleton({ className }: { className?: string }) {
  return (
    <span
      aria-hidden
      className={cn('inline-block rounded-sm animate-skeleton', className)}
    />
  )
}
