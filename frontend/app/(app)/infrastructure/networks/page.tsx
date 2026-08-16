import Link from 'next/link'
import { Network as NetworkIcon } from 'lucide-react'

import { EmptyState, Mono, PageHeader, Panel } from '@/components/ui/panel'
import { networks } from '@/lib/api/mock'

export const metadata = { title: 'Networks' }

export default function NetworksPage() {
  return (
    <div className="mx-auto max-w-6xl">
      <PageHeader
        title="Networks"
        description="Docker networks on this host. Workloads on the same network can reach each other by name."
      />

      {networks.length === 0 ? (
        <EmptyState
          icon={NetworkIcon}
          title="No networks"
          description="A default bridge is created with the host. Provision a database or application to attach something."
        />
      ) : (
        <div className="flex flex-col gap-4">
          {networks.map((n) => (
            <Panel
              key={n.id}
              title={
                <span className="flex items-center gap-2">
                  <span className="font-mono">{n.name}</span>
                  <span className="rounded bg-secondary px-1.5 py-0.5 font-mono text-[11px] font-normal text-muted-foreground">
                    {n.driver}
                  </span>
                </span>
              }
              description={
                <span className="font-mono">
                  {n.subnet} · gw {n.gateway}
                </span>
              }
              bodyClassName="p-0"
            >
              {n.attached.length === 0 ? (
                <p className="px-4 py-6 text-sm text-muted-foreground">Nothing attached.</p>
              ) : (
                <ul className="divide-y divide-border">
                  {n.attached.map((a) => {
                    const href = a.kind === 'database' ? `/databases/${a.id}` : `/applications/${a.id}`
                    return (
                      <li key={`${n.id}-${a.id}`} className="flex items-center justify-between gap-3 px-4 py-2.5">
                        <Link href={href} className="min-w-0 hover:text-primary">
                          <p className="truncate text-sm font-medium text-foreground">{a.name}</p>
                          <p className="text-xs text-muted-foreground">{a.kind}</p>
                        </Link>
                        <Mono className="shrink-0 text-muted-foreground">{a.ip ?? '—'}</Mono>
                      </li>
                    )
                  })}
                </ul>
              )}
            </Panel>
          ))}
        </div>
      )}
    </div>
  )
}
