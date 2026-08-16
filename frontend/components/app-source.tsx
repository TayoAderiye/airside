import { GitBranch, Container, FileCode2, Layers } from 'lucide-react'
import type { AppSummary, AppSourceKind } from '@/lib/api/types'
import { cn } from '@/lib/utils'

const SOURCE_META: Record<AppSourceKind, { label: string; icon: typeof GitBranch; className: string }> = {
  git: { label: 'Git', icon: GitBranch, className: 'bg-accent/15 text-accent border-accent/40' },
  image: { label: 'Image', icon: Container, className: 'bg-transitional/15 text-transitional border-transitional/40' },
  dockerfile: { label: 'Dockerfile', icon: FileCode2, className: 'bg-degraded/15 text-degraded border-degraded/40' },
  compose: { label: 'Compose', icon: Layers, className: 'bg-running/15 text-running border-running/40' },
}

/** Bordered source glyph, mirroring the database engine glyph treatment. */
export function AppSourceGlyph({ source, className }: { source: AppSourceKind; className?: string }) {
  const meta = SOURCE_META[source]
  const Icon = meta.icon
  return (
    <span
      className={cn('grid shrink-0 place-items-center rounded-md border', meta.className, className ?? 'size-8')}
      aria-hidden
    >
      <Icon className="size-1/2" />
    </span>
  )
}

/** The human-readable origin of an app, e.g. "acme/api@main" or an image tag. */
export function appSourceLabel(app: AppSummary) {
  if (app.source === 'image') return app.image ?? 'image'
  if (app.repo) return app.branch ? `${app.repo}@${app.branch}` : app.repo
  return SOURCE_META[app.source].label
}
