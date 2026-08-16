'use client'

import { useEffect, useMemo, useState } from 'react'
import { Loader2, Shield } from 'lucide-react'

import { PageHeader, Panel } from '@/components/ui/panel'
import { ProblemBanner } from '@/components/problem-banner'
import { client } from '@/lib/api/client'
import type { components } from '@/lib/api/schema'
import { formatRelative } from '@/lib/status'
import { cn } from '@/lib/utils'

type User = components['schemas']['UserDto']
type Role = components['schemas']['RoleDto']
type Permission = components['schemas']['PermissionDto']

/**
 * The group a permission belongs to, taken from its code.
 *
 * PermissionDto has no group — the codes are `database.read`, `application.deploy`
 * and so on, so the prefix is the grouping and deriving it cannot drift from the
 * API the way a hand-written table does. What this replaced kept six group names
 * and a fixed list of permissions, neither of which the API had ever confirmed.
 */
function groupOf(code: string) {
  const dot = code.indexOf('.')
  return dot < 0 ? code : code.slice(0, dot)
}

export function AccessView() {
  const [users, setUsers] = useState<User[] | null>(null)
  const [roles, setRoles] = useState<Role[]>([])
  const [permissions, setPermissions] = useState<Permission[]>([])
  const [selected, setSelected] = useState<string | null>(null)
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    let cancelled = false

    Promise.all([
      client.GET('/api/v1/users'),
      client.GET('/api/v1/roles'),
      client.GET('/api/v1/permissions'),
    ])
      .then(([userRes, roleRes, permRes]) => {
        if (cancelled) return

        const roleList = roleRes.data ?? []
        setUsers(userRes.data?.items ?? [])
        setRoles(roleList)

        // Obsolete permissions still exist so old audit entries resolve, but
        // showing them in an editor would invite granting something retired.
        setPermissions((permRes.data ?? []).filter((p) => !p.isObsolete))
        setSelected((prev) => prev ?? roleList[0]?.slug ?? null)
      })
      .catch((err) => {
        if (cancelled) return
        setError(err)
        setUsers([])
      })

    return () => {
      cancelled = true
    }
  }, [])

  const role = roles.find((r) => r.slug === selected) ?? null
  const granted = useMemo(() => new Set(role?.permissions ?? []), [role])

  const groups = useMemo(() => {
    const byGroup = new Map<string, Permission[]>()

    for (const p of permissions) {
      const key = groupOf(p.code)
      const list = byGroup.get(key) ?? []
      list.push(p)
      byGroup.set(key, list)
    }

    return [...byGroup.entries()].sort(([a], [b]) => a.localeCompare(b))
  }, [permissions])

  if (users === null) {
    return (
      <p className="flex items-center gap-2 text-sm text-muted-foreground">
        <Loader2 className="size-4 animate-spin text-transitional" />
        Loading users and roles…
      </p>
    )
  }

  return (
    <div className="mx-auto max-w-6xl">
      <PageHeader
        title="Users & Access"
        description="Roles are bundles over granular permissions. Restarting a database and reading its contents are different grants."
      />

      {error != null && <ProblemBanner error={error} />}

      <Panel bodyClassName="p-0" className="mb-6">
        {users.length === 0 ? (
          <p className="px-4 py-8 text-center text-sm text-muted-foreground">No users.</p>
        ) : (
          <ul className="divide-y divide-border">
            {users.map((u) => (
              <li key={u.id} className="flex flex-col gap-2 px-4 py-3 sm:flex-row sm:items-center sm:gap-4">
                <div className="flex min-w-0 flex-1 items-center gap-3">
                  <span className="grid size-8 place-items-center rounded-full bg-primary/15 font-mono text-xs font-semibold text-primary">
                    {initials(u.displayName || u.email)}
                  </span>
                  <div className="min-w-0">
                    <p className="truncate text-sm font-medium text-foreground">{u.displayName || u.email}</p>
                    <p className="truncate font-mono text-xs text-muted-foreground">{u.email}</p>
                  </div>
                </div>

                {/* Roles, plural. A user can hold several, and rendering only
                    the first would hide a grant somebody made deliberately. */}
                <div className="flex flex-wrap gap-1">
                  {u.roles.length === 0 ? (
                    <span className="font-mono text-xs text-muted-foreground">no role</span>
                  ) : (
                    u.roles.map((r) => (
                      <button
                        key={r}
                        type="button"
                        onClick={() => setSelected(r)}
                        className="w-fit rounded bg-secondary px-2 py-0.5 font-mono text-xs text-foreground hover:bg-secondary/80"
                      >
                        {r}
                      </button>
                    ))
                  )}
                </div>

                <span
                  className={cn('text-xs font-medium', u.isActive ? 'text-running' : 'text-muted-foreground')}
                >
                  {u.isActive ? 'Active' : 'Disabled'}
                </span>
                <span className="font-mono text-xs text-muted-foreground sm:w-24 sm:text-right">
                  {u.lastLoginAt ? formatRelative(u.lastLoginAt) : 'never'}
                </span>
              </li>
            ))}
          </ul>
        )}
      </Panel>

      <Panel
        title={
          <span className="flex items-center gap-2">
            <Shield className="size-4 text-muted-foreground" />
            Permissions by role
          </span>
        }
        description="Infrastructure permissions move containers. Query permissions read or write what is inside them. A role can have one without the other."
      >
        <div className="mb-4 flex flex-wrap gap-1.5">
          {roles.map((r) => (
            <button
              key={r.slug}
              type="button"
              onClick={() => setSelected(r.slug)}
              aria-pressed={selected === r.slug}
              className={cn(
                'rounded-md border px-2.5 py-1 text-xs transition-colors',
                selected === r.slug
                  ? 'border-primary bg-primary/10 text-foreground'
                  : 'border-border text-muted-foreground hover:text-foreground',
              )}
            >
              {r.name}
            </button>
          ))}
        </div>

        {role?.description && <p className="mb-3 text-sm text-muted-foreground">{role.description}</p>}

        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {groups.map(([name, defs]) => (
            <div key={name} className="rounded-lg border border-border p-3">
              <p className="mb-1.5 font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{name}</p>
              <ul className="flex flex-col gap-1.5">
                {defs.map((p) => {
                  const on = granted.has(p.code)
                  return (
                    <li key={p.code} className="flex items-start gap-2">
                      <span
                        className={cn(
                          'mt-0.5 grid size-4 shrink-0 place-items-center rounded-sm border font-mono text-[10px]',
                          on ? 'border-running/50 bg-running-soft text-running' : 'border-border text-muted-foreground',
                        )}
                        aria-hidden
                      >
                        {on ? '✓' : ''}
                      </span>
                      <span className={cn('min-w-0 text-sm', on ? 'text-foreground' : 'text-muted-foreground')}>
                        <span className="font-mono text-xs">{p.code}</span>
                        {p.description && <span className="block text-xs text-muted-foreground">{p.description}</span>}
                      </span>
                    </li>
                  )
                })}
              </ul>
            </div>
          ))}
        </div>
      </Panel>
    </div>
  )
}

function initials(name: string) {
  return name
    .split(/[\s@.]+/)
    .map((p) => p[0])
    .filter(Boolean)
    .slice(0, 2)
    .join('')
    .toUpperCase()
}
