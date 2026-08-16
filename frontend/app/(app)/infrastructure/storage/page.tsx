import Link from 'next/link'
import { HardDrive, Unplug } from 'lucide-react'

import { ResourceMeter } from '@/components/resource-meter'
import { EmptyState, Mono, PageHeader, Panel } from '@/components/ui/panel'
import { volumes } from '@/lib/api/mock'
import { formatRelative } from '@/lib/status'

export const metadata = { title: 'Storage' }

export default function StoragePage() {
  const used = volumes.reduce((n, v) => n + v.usedGiB, 0)
  const limit = volumes.reduce((n, v) => n + v.limitGiB, 0)
  const orphans = volumes.filter((v) => !v.attachedTo)

  return (
    <div className="mx-auto max-w-6xl">
      <PageHeader
        title="Storage"
        description={`${volumes.length} volumes · ${used.toFixed(0)} of ${limit} GiB reserved. Orphan volumes still occupy disk.`}
      />

      {orphans.length > 0 && (
        <div className="mb-4 rounded-lg border border-degraded/40 bg-degraded-soft/50 px-4 py-3">
          <p className="text-sm font-medium text-degraded">
            {orphans.length} volume{orphans.length > 1 ? 's' : ''} not attached to any workload
          </p>
          <p className="mt-0.5 text-sm text-foreground/80">
            {orphans.map((v) => v.name).join(', ')} — delete them if the data is gone, or attach them.
          </p>
        </div>
      )}

      {volumes.length === 0 ? (
        <EmptyState
          icon={HardDrive}
          title="No volumes yet"
          description="Volumes appear when you provision a database or attach storage to an application."
        />
      ) : (
        <Panel bodyClassName="p-0">
          <ul className="divide-y divide-border">
            {volumes.map((v) => {
              const href =
                v.attachedType === 'database'
                  ? `/databases/${mockDbId(v.attachedTo)}`
                  : v.attachedType === 'application'
                    ? `/applications/${mockAppId(v.attachedTo)}`
                    : undefined
              return (
                <li key={v.id} className="flex flex-col gap-3 p-4 sm:flex-row sm:items-center sm:gap-6">
                  <div className="flex min-w-0 flex-1 items-start gap-3">
                    <span className="grid size-9 shrink-0 place-items-center rounded-md bg-secondary text-muted-foreground">
                      {v.attachedTo ? <HardDrive className="size-4" /> : <Unplug className="size-4 text-degraded" />}
                    </span>
                    <div className="min-w-0">
                      <p className="truncate font-mono text-sm text-foreground">{v.name}</p>
                      <p className="mt-0.5 flex flex-wrap items-center gap-x-2 text-xs text-muted-foreground">
                        <span className="font-mono">{v.driver}</span>
                        {v.mountPath && (
                          <>
                            <span aria-hidden>·</span>
                            <Mono>{v.mountPath}</Mono>
                          </>
                        )}
                        <span aria-hidden>·</span>
                        <span>created {formatRelative(v.createdAt)}</span>
                      </p>
                      {v.attachedTo && href ? (
                        <Link href={href} className="mt-1 inline-block font-mono text-xs text-primary hover:underline">
                          {v.attachedTo}
                        </Link>
                      ) : (
                        <p className="mt-1 text-xs text-degraded">unattached</p>
                      )}
                    </div>
                  </div>
                  <div className="w-full sm:w-56">
                    <ResourceMeter label="Used" used={v.usedGiB} limit={v.limitGiB} unit="GiB" />
                  </div>
                </li>
              )
            })}
          </ul>
        </Panel>
      )}
    </div>
  )
}

function mockDbId(name?: string) {
  const map: Record<string, string> = {
    'payments-primary': 'db_pg_main',
    'analytics-store': 'db_mysql_analytics',
    'events-log': 'db_mongo_events',
    'staging-db': 'db_pg_staging',
    'session-cache': 'db_redis_cache',
  }
  return name ? map[name] ?? name : ''
}

function mockAppId(name?: string) {
  const map: Record<string, string> = {
    'payments-api': 'app_api',
    'dashboard-web': 'app_web',
    'queue-worker': 'app_worker',
    'docs-site': 'app_docs',
  }
  return name ? map[name] ?? name : ''
}
