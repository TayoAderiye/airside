'use client'

import Link from 'next/link'
import { usePathname } from 'next/navigation'

import { BrandMark } from '@/components/brand/mark'
import { navGroups } from './nav-config'
import { cn } from '@/lib/utils'

export function SidebarContent({ onNavigate }: { onNavigate?: () => void }) {
  const pathname = usePathname()

  return (
    <div className="flex h-full flex-col">
      <div className="flex h-12 items-center gap-2 border-b border-border px-3">
        <BrandMark />
      </div>

      <nav className="flex-1 overflow-y-auto px-2 py-2.5" aria-label="Primary">
        {navGroups.map((group, i) => (
          <div
            key={group.heading ?? i}
            className={cn(i > 0 && 'mt-3 border-t border-border/80 pt-3')}
          >
            {group.heading && (
              <p className="px-2.5 pb-1.5 font-mono text-[10px] font-medium uppercase tracking-[0.14em] text-muted-foreground/80">
                {group.heading}
              </p>
            )}
            <ul className="flex flex-col gap-px">
              {group.items.map((item) => {
                const active =
                  pathname === item.href || pathname.startsWith(item.href + '/')
                const Icon = item.icon
                return (
                  <li key={item.href}>
                    <Link
                      href={item.href}
                      onClick={onNavigate}
                      aria-current={active ? 'page' : undefined}
                      className={cn(
                        'group relative flex items-center gap-2 rounded-md px-2.5 py-1.5 text-[13px] transition-colors',
                        active
                          ? 'bg-secondary font-medium text-foreground'
                          : 'text-muted-foreground hover:bg-secondary/60 hover:text-foreground',
                      )}
                    >
                      {active && (
                        <span className="absolute left-0 top-1/2 h-3.5 w-0.5 -translate-y-1/2 rounded-full bg-primary" />
                      )}
                      <Icon className="size-3.5 shrink-0" aria-hidden />
                      {item.label}
                    </Link>
                  </li>
                )
              })}
            </ul>
          </div>
        ))}
      </nav>

      <div className="border-t border-border px-3 py-2.5">
        {/*
          The version baked in at build time, not a literal. This read "v0.2"
          against a 0.1.3 build — a number nothing in the product had ever been.
        */}
        <p className="font-mono text-[10px] text-muted-foreground">
          v{process.env.NEXT_PUBLIC_AIRSIDE_UI_VERSION ?? 'dev'} · /api/v1
        </p>
      </div>
    </div>
  )
}
