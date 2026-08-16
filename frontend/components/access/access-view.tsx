'use client'

import { useState } from 'react'
import { Shield, UserPlus } from 'lucide-react'

import { Button } from '@/components/ui/button'
import { PageHeader, Panel } from '@/components/ui/panel'
import { accessUsers, permissions, rolePermissions } from '@/lib/api/mock'
import type { AccessUser, RoleName } from '@/lib/api/types'
import { formatRelative } from '@/lib/status'
import { cn } from '@/lib/utils'

const ROLES: RoleName[] = [
  'Super Admin',
  'Infrastructure Admin',
  'Database Admin',
  'Application Admin',
  'Developer',
  'Read Only',
]

const GROUPS = ['Infrastructure', 'Databases', 'Applications', 'Data & Query', 'Secrets', 'Access'] as const

const STATUS: Record<AccessUser['status'], { label: string; className: string }> = {
  active: { label: 'Active', className: 'text-running' },
  invited: { label: 'Invited', className: 'text-degraded' },
  disabled: { label: 'Disabled', className: 'text-muted-foreground' },
}

export function AccessView() {
  const [selectedRole, setSelectedRole] = useState<RoleName>('Infrastructure Admin')
  const granted = new Set(rolePermissions[selectedRole])

  return (
    <div className="mx-auto max-w-6xl">
      <PageHeader
        title="Users & Access"
        description="Roles are bundles over granular permissions. Restarting a database and reading its contents are different grants."
        actions={
          <Button variant="default">
            <UserPlus className="size-4" /> Invite
          </Button>
        }
      />

      <Panel bodyClassName="p-0" className="mb-6">
        <ul className="divide-y divide-border">
          {accessUsers.map((u) => {
            const s = STATUS[u.status]
            return (
              <li key={u.id} className="flex flex-col gap-2 px-4 py-3 sm:flex-row sm:items-center sm:gap-4">
                <div className="flex min-w-0 flex-1 items-center gap-3">
                  <span className="grid size-8 place-items-center rounded-full bg-primary/15 font-mono text-xs font-semibold text-primary">
                    {initials(u.name)}
                  </span>
                  <div className="min-w-0">
                    <p className="truncate text-sm font-medium text-foreground">{u.name}</p>
                    <p className="truncate font-mono text-xs text-muted-foreground">{u.email}</p>
                  </div>
                </div>
                <button
                  type="button"
                  onClick={() => setSelectedRole(u.role)}
                  className="w-fit rounded bg-secondary px-2 py-0.5 font-mono text-xs text-foreground hover:bg-secondary/80"
                >
                  {u.role}
                </button>
                <span className={cn('text-xs font-medium', s.className)}>{s.label}</span>
                <span className="font-mono text-xs text-muted-foreground sm:w-24 sm:text-right">
                  {u.lastActive ? formatRelative(u.lastActive) : 'never'}
                </span>
              </li>
            )
          })}
        </ul>
      </Panel>

      <Panel
        title={
          <span className="flex items-center gap-2">
            <Shield className="size-4 text-muted-foreground" />
            Permission editor
          </span>
        }
        description="Infrastructure permissions move containers. Data & Query permissions read or write what is inside them. A role can have one without the other."
      >
        <div className="mb-4 flex flex-wrap gap-1.5">
          {ROLES.map((role) => (
            <button
              key={role}
              type="button"
              onClick={() => setSelectedRole(role)}
              aria-pressed={selectedRole === role}
              className={cn(
                'rounded-md border px-2.5 py-1 text-xs transition-colors',
                selectedRole === role
                  ? 'border-primary bg-primary/10 text-foreground'
                  : 'border-border text-muted-foreground hover:text-foreground',
              )}
            >
              {role}
            </button>
          ))}
        </div>

        <div className="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <PermissionColumn
            title="Infrastructure"
            note="Lifecycle: start, stop, resize, deploy. Does not grant access to data."
            groups={['Infrastructure', 'Databases', 'Applications']}
            granted={granted}
          />
          <PermissionColumn
            title="Data & query"
            note="Read or write the contents of a database. Separate from being able to restart it."
            groups={['Data & Query', 'Secrets', 'Access']}
            granted={granted}
            isolate
          />
        </div>
      </Panel>
    </div>
  )
}

function PermissionColumn({
  title,
  note,
  groups,
  granted,
  isolate,
}: {
  title: string
  note: string
  groups: readonly (typeof GROUPS)[number][]
  granted: Set<string>
  isolate?: boolean
}) {
  return (
    <div className={cn('rounded-lg border p-3', isolate ? 'border-degraded/30 bg-degraded-soft/20' : 'border-border')}>
      <p className="text-sm font-medium text-foreground">{title}</p>
      <p className="mt-0.5 text-xs text-muted-foreground">{note}</p>
      <div className="mt-3 flex flex-col gap-3">
        {groups.map((group) => {
          const defs = permissions.filter((p) => p.group === group)
          return (
            <div key={group}>
              <p className="mb-1.5 font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{group}</p>
              <ul className="flex flex-col gap-1.5">
                {defs.map((p) => {
                  const on = granted.has(p.key)
                  return (
                    <li key={p.key} className="flex items-center gap-2">
                      <span
                        className={cn(
                          'grid size-4 place-items-center rounded-sm border font-mono text-[10px]',
                          on ? 'border-running/50 bg-running-soft text-running' : 'border-border text-muted-foreground',
                        )}
                        aria-hidden
                      >
                        {on ? '✓' : ''}
                      </span>
                      <span className={cn('text-sm', on ? 'text-foreground' : 'text-muted-foreground')}>{p.label}</span>
                    </li>
                  )
                })}
              </ul>
            </div>
          )
        })}
      </div>
    </div>
  )
}

function initials(name: string) {
  return name
    .split(' ')
    .map((p) => p[0])
    .slice(0, 2)
    .join('')
    .toUpperCase()
}
