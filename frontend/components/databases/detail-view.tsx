'use client'

import { useState } from 'react'
import Link from 'next/link'
import { useSearchParams } from 'next/navigation'
import { Play, Square, RotateCw, DatabaseBackup, SquareTerminal, Trash2 } from 'lucide-react'
import type { DatabaseSummary, RedisStats } from '@/lib/api/types'
import { Panel } from '@/components/ui/panel'
import { Tabs } from '@/components/ui/tabs'
import { StatusBadge } from '@/components/status-badge'
import { EngineGlyph, engineLabel } from '@/components/engine'
import { ResourceMeter } from '@/components/resource-meter'
import { CopyField } from '@/components/ui/copy-field'
import { LogStream } from '@/components/logs/log-stream'
import { ConfirmDialog } from '@/components/confirm-dialog'
import { Button, buttonVariants } from '@/components/ui/button'
import { cn } from '@/lib/utils'

const PORTS: Record<string, number> = { postgres: 5432, mysql: 3306, mongodb: 27017, redis: 6379 }

export function DatabaseDetailView({ db, redis }: { db: DatabaseSummary; redis?: RedisStats }) {
  const search = useSearchParams()
  const [tab, setTab] = useState(search.get('tab') === 'query' ? 'query' : 'overview')
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [confirmStop, setConfirmStop] = useState(false)
  const running = db.state === 'Running' || db.state === 'Unhealthy'

  const connString =
    db.engine === 'redis'
      ? `redis://:••••••@ip-10-0-3-14:${PORTS[db.engine]}/0`
      : `${db.engine}://app:••••••@ip-10-0-3-14:${PORTS[db.engine]}/${db.name.replace(/-/g, '_')}`

  const tabs = [
    { id: 'overview', label: 'Overview' },
    ...(db.engine === 'redis' ? [{ id: 'redis', label: 'Redis' }] : []),
    { id: 'logs', label: 'Logs' },
    { id: 'backups', label: 'Backups' },
    { id: 'config', label: 'Configuration' },
    { id: 'users', label: 'Users' },
    { id: 'danger', label: 'Danger zone' },
  ]

  return (
    <div className="flex flex-col gap-6">
      {/* header */}
      <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div className="flex items-center gap-3">
          <EngineGlyph engine={db.engine} className="size-11" />
          <div>
            <div className="flex items-center gap-3">
              <h1 className="font-display text-2xl font-semibold text-foreground">{db.name}</h1>
              <StatusBadge state={db.state} />
            </div>
            <p className="font-mono text-sm text-muted-foreground">
              {engineLabel(db.engine)} {db.version} · {db.connections} connections
            </p>
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-2">
          {running ? (
            <Button variant="outline" size="sm" onClick={() => setConfirmStop(true)}>
              <Square className="size-3.5" /> Stop
            </Button>
          ) : (
            <Button variant="outline" size="sm">
              <Play className="size-3.5" /> Start
            </Button>
          )}
          <Button variant="outline" size="sm">
            <RotateCw className="size-3.5" /> Restart
          </Button>
          <Button variant="outline" size="sm" onClick={() => setTab('query')}>
            <SquareTerminal className="size-3.5" /> Query
          </Button>
          <Button variant="outline" size="sm">
            <DatabaseBackup className="size-3.5" /> Back up now
          </Button>
        </div>
      </div>

      <Tabs tabs={tabs} active={tab} onChange={setTab} />

      {tab === 'overview' && (
        <div className="grid grid-cols-1 gap-6 lg:grid-cols-3">
          <Panel title="Resources" className="lg:col-span-2">
            <div className="grid grid-cols-1 gap-5 sm:grid-cols-3">
              <ResourceMeter label="CPU" used={db.cpu.used} limit={db.cpu.limit} unit="cores" />
              <ResourceMeter label="Memory" used={db.memory.used} limit={db.memory.limit} unit="GiB" />
              {db.engine !== 'redis' && (
                <ResourceMeter label="Storage" used={db.storage.used} limit={db.storage.limit} unit="GiB" />
              )}
            </div>
          </Panel>
          <Panel title="Connection">
            <p className="mb-2 text-xs text-muted-foreground">Password is masked. Reveal only when needed.</p>
            <CopyField value={connString} masked />
          </Panel>
        </div>
      )}

      {tab === 'redis' && redis && <RedisPanel redis={redis} />}

      {tab === 'logs' && (
        <Panel title="Logs" bodyClassName="p-0">
          <LogStream source={db.name} />
        </Panel>
      )}

      {tab === 'backups' && (
        <Panel
          title="Backups"
          description={
            db.engine === 'redis'
              ? 'Redis used only as a cache may not need backups. AOF is the persistence that matters here.'
              : 'Scheduled and manual backups for this database.'
          }
        >
          <p className="text-sm text-muted-foreground">
            Policies live under Backups. Restore requires typing this database name and offers a pre-restore snapshot.
          </p>
          <div className="mt-3">
            <Link href="/backups" className={cn(buttonVariants({ variant: 'outline', size: 'sm' }), 'gap-1.5')}>
              <DatabaseBackup className="size-3.5" /> Open backup policies
            </Link>
          </div>
        </Panel>
      )}

      {tab === 'config' && (
        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <Panel title="Limits">
            <div className="flex flex-col gap-3">
              <p className="text-sm text-muted-foreground">
                Resize CPU and memory without recreating the instance. Storage can only increase.
              </p>
              <div className="flex flex-wrap gap-2">
                <Button variant="outline" size="sm">
                  Resize CPU / memory
                </Button>
                <Button variant="outline" size="sm">
                  Increase storage
                </Button>
              </div>
            </div>
          </Panel>
          <Panel title="Credentials">
            <p className="mb-3 text-sm text-muted-foreground">Rotate writes a new password and an audit entry.</p>
            <CopyField value={connString} masked />
            <div className="mt-3">
              <Button variant="outline" size="sm">
                Rotate credentials
              </Button>
            </div>
          </Panel>
        </div>
      )}

      {tab === 'users' && (
        <Panel title="Database users" description="Who can connect. Separate from Airside operator roles.">
          {db.engine === 'redis' ? (
            <p className="text-sm text-muted-foreground">
              Default Redis has no username. Access is the instance password. ACL users are not configured.
            </p>
          ) : (
            <ul className="divide-y divide-border rounded-md border border-border">
              <li className="flex items-center justify-between px-3 py-2">
                <span className="font-mono text-sm">app</span>
                <span className="text-xs text-muted-foreground">login · {db.name.replace(/-/g, '_')}</span>
              </li>
              <li className="flex items-center justify-between px-3 py-2">
                <span className="font-mono text-sm">readonly</span>
                <span className="text-xs text-muted-foreground">SELECT only</span>
              </li>
            </ul>
          )}
        </Panel>
      )}

      {tab === 'danger' && (
        <div className="flex flex-col gap-4">
          <Panel title="Stop database" className="border-degraded/30">
            <div className="flex items-center justify-between gap-4">
              <p className="text-sm text-muted-foreground">
                Stops the container. Data is preserved; connections drop immediately.
              </p>
              <Button variant="outline" size="sm" onClick={() => setConfirmStop(true)} disabled={!running}>
                Stop
              </Button>
            </div>
          </Panel>
          <Panel title="Delete database" className="border-failed/40">
            <div className="flex items-center justify-between gap-4">
              <p className="text-sm text-muted-foreground">
                Permanently removes the database and its volumes. This cannot be undone.
              </p>
              <Button variant="destructive" size="sm" onClick={() => setConfirmDelete(true)}>
                <Trash2 className="size-3.5" /> Delete
              </Button>
            </div>
          </Panel>
        </div>
      )}

      <ConfirmDialog
        open={confirmStop}
        onOpenChange={setConfirmStop}
        title={`Stop ${db.name}?`}
        description="Active connections will be dropped. The database can be started again at any time."
        confirmLabel="Stop database"
        tone="warn"
      />
      <ConfirmDialog
        open={confirmDelete}
        onOpenChange={setConfirmDelete}
        title={`Delete ${db.name}?`}
        description="Removes the database container. Data on the volume is kept unless you opt in below. These are different decisions."
        confirmLabel="Delete database"
        tone="danger"
        requireTyped={db.name}
        extraConfirms={[
          {
            id: 'destroyVolume',
            label: 'Also delete the data volume',
            description: 'Destroys all data. Off by default — deleting the workload does not imply destroying the data.',
            danger: true,
            defaultChecked: false,
          },
        ]}
      />
    </div>
  )
}

function RedisPanel({ redis }: { redis: RedisStats }) {
  const memRatio = redis.maxMemory > 0 ? redis.memoryUsed / redis.maxMemory : 0
  return (
    <div className="grid grid-cols-1 gap-6 lg:grid-cols-3">
      <Panel title="Memory" className="lg:col-span-2">
        <ResourceMeter
          label="Used / max memory"
          used={redis.memoryUsed}
          limit={redis.maxMemory}
          unit="GiB"
        />
        <dl className="mt-4 grid grid-cols-2 gap-4 sm:grid-cols-4">
          <Stat k="Policy" v={redis.maxMemoryPolicy} mono />
          <Stat k="AOF" v={redis.aofEnabled ? 'enabled' : 'disabled'} tone={redis.aofEnabled ? 'good' : 'warn'} />
          <Stat k="Evicted keys" v={redis.evictedKeys.toLocaleString()} tone={redis.evictedKeys > 10000 ? 'warn' : undefined} />
          <Stat k="Keyspace" v={redis.keyspaceSize.toLocaleString()} />
        </dl>
        {memRatio >= 0.85 && (
          <p className="mt-3 rounded-md border border-degraded/30 bg-degraded-soft px-3 py-2 text-xs text-degraded">
            Memory is near max ({Math.round(memRatio * 100)}%). With <span className="font-mono">{redis.maxMemoryPolicy}</span>,
            keys will be evicted as it fills.
          </p>
        )}
      </Panel>
      <Panel title="Cache performance">
        <div className="flex flex-col gap-4">
          <div>
            <p className="text-xs uppercase tracking-wide text-muted-foreground">Hit rate</p>
            <p className={cn('font-display text-3xl font-semibold', redis.hitRate >= 0.9 ? 'text-running' : 'text-degraded')}>
              {(redis.hitRate * 100).toFixed(1)}%
            </p>
          </div>
          <Stat k="Connected clients" v={redis.connectedClients.toLocaleString()} />
        </div>
      </Panel>
    </div>
  )
}

function Stat({ k, v, mono, tone }: { k: string; v: string; mono?: boolean; tone?: 'good' | 'warn' }) {
  return (
    <div>
      <dt className="text-xs uppercase tracking-wide text-muted-foreground">{k}</dt>
      <dd
        className={cn(
          'mt-0.5 text-sm text-foreground',
          mono && 'font-mono',
          tone === 'good' && 'text-running',
          tone === 'warn' && 'text-degraded',
        )}
      >
        {v}
      </dd>
    </div>
  )
}
