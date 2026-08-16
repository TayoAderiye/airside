import { cn } from '@/lib/utils'

/**
 * Card surface. When `title` is supplied it renders a bordered header; the
 * body is padded by default (opt out with bodyClassName="p-0" for edge-to-edge
 * content like log viewers). This keeps every screen's panels consistent.
 */
export function Panel({
  title,
  description,
  actions,
  children,
  className,
  bodyClassName,
}: {
  title?: React.ReactNode
  description?: React.ReactNode
  actions?: React.ReactNode
  children?: React.ReactNode
  className?: string
  bodyClassName?: string
}) {
  return (
    <section className={cn('elev-card overflow-hidden rounded-lg border border-border bg-card', className)}>
      {title && <PanelHeader title={title} description={description} actions={actions} />}
      <div className={cn('p-3', bodyClassName)}>{children}</div>
    </section>
  )
}

export function PanelHeader({
  title,
  description,
  actions,
  className,
}: {
  title: React.ReactNode
  description?: React.ReactNode
  actions?: React.ReactNode
  className?: string
}) {
  return (
    <div
      className={cn(
        'flex items-start justify-between gap-3 border-b border-border px-3 py-2.5',
        className,
      )}
    >
      <div className="min-w-0">
        <h2 className="font-display text-sm font-semibold text-foreground">{title}</h2>
        {description && (
          <p className="mt-0.5 text-xs text-muted-foreground">{description}</p>
        )}
      </div>
      {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
    </div>
  )
}

export function PageHeader({
  title,
  description,
  actions,
}: {
  title: React.ReactNode
  description?: React.ReactNode
  actions?: React.ReactNode
}) {
  return (
    <div className="mb-4 flex flex-col gap-2 sm:flex-row sm:items-end sm:justify-between">
      <div className="min-w-0">
        <h1 className="text-balance font-display text-xl font-semibold tracking-tight text-foreground">
          {title}
        </h1>
        {description && (
          <p className="mt-0.5 max-w-2xl text-pretty text-sm text-muted-foreground">
            {description}
          </p>
        )}
      </div>
      {actions && <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}</div>}
    </div>
  )
}

export function EmptyState({
  icon: Icon,
  title,
  description,
  action,
}: {
  icon?: React.ComponentType<{ className?: string }>
  title: string
  description: string
  action?: React.ReactNode
}) {
  return (
    <div className="flex flex-col items-center justify-center rounded-lg border border-dashed border-border bg-card/40 px-5 py-10 text-center">
      {Icon && (
        <span className="mb-2.5 grid size-9 place-items-center rounded-md bg-secondary text-muted-foreground">
          <Icon className="size-4" />
        </span>
      )}
      <p className="font-display text-sm font-medium text-foreground">{title}</p>
      <p className="mt-1 max-w-sm text-pretty text-xs text-muted-foreground">{description}</p>
      {action && <div className="mt-3">{action}</div>}
    </div>
  )
}

/** Inline error surface — says what happened and what to do, no apology. */
export function ErrorState({
  title,
  detail,
  action,
}: {
  title: string
  detail: string
  action?: React.ReactNode
}) {
  return (
    <div className="rounded-md border border-failed/30 bg-failed-soft/50 px-3 py-2.5">
      <p className="text-sm font-medium text-failed">{title}</p>
      <p className="mt-0.5 text-sm text-foreground/80">{detail}</p>
      {action && <div className="mt-3">{action}</div>}
    </div>
  )
}

export function Mono({
  className,
  children,
}: {
  className?: string
  children: React.ReactNode
}) {
  return <span className={cn('font-mono text-xs', className)}>{children}</span>
}

/** Label/value pair for detail panels. */
export function StatItem({
  label,
  value,
  mono,
  tone,
}: {
  label: string
  value: React.ReactNode
  mono?: boolean
  tone?: 'good' | 'warn' | 'bad'
}) {
  const toneClass =
    tone === 'good' ? 'text-running' : tone === 'warn' ? 'text-degraded' : tone === 'bad' ? 'text-failed' : 'text-foreground'
  return (
    <div className="flex items-center justify-between gap-4">
      <span className="text-xs text-muted-foreground">{label}</span>
      <span className={cn('text-sm', mono && 'font-mono', toneClass)}>{value}</span>
    </div>
  )
}
