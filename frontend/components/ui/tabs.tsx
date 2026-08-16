'use client'

import { cn } from '@/lib/utils'

export function Tabs({
  tabs,
  active,
  onChange,
}: {
  tabs: { id: string; label: string; badge?: number }[]
  active: string
  onChange: (id: string) => void
}) {
  return (
    <div role="tablist" className="flex gap-1 border-b border-border">
      {tabs.map((t) => (
        <button
          key={t.id}
          role="tab"
          aria-selected={active === t.id}
          onClick={() => onChange(t.id)}
          className={cn(
            'relative -mb-px inline-flex items-center gap-2 border-b-2 px-3 py-2 text-sm transition-colors',
            active === t.id
              ? 'border-primary text-foreground'
              : 'border-transparent text-muted-foreground hover:text-foreground',
          )}
        >
          {t.label}
          {t.badge != null && (
            <span className="rounded-full bg-secondary px-1.5 text-[11px] font-medium text-muted-foreground">
              {t.badge}
            </span>
          )}
        </button>
      ))}
    </div>
  )
}
