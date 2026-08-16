'use client'

import { useEffect, useState } from 'react'

import { ConfirmDialog } from '@/components/confirm-dialog'
import { ProblemBanner } from '@/components/problem-banner'
import { Button } from '@/components/ui/button'
import { Field, TextInput } from '@/components/ui/field'
import { PageHeader, Panel } from '@/components/ui/panel'
import { client } from '@/lib/api/client'
import { ApiError } from '@/lib/api/problem'
import type { components } from '@/lib/api/schema'
import { formatRelative } from '@/lib/status'

type User = components['schemas']['UserDto']
type Role = components['schemas']['RoleDto']
type Audit = components['schemas']['AuditEventDto']
type Dashboard = components['schemas']['DashboardDomainDto']

export function SettingsView() {
  const [users, setUsers] = useState<User[]>([])
  const [roles, setRoles] = useState<Role[]>([])
  const [audit, setAudit] = useState<Audit[]>([])
  const [dashboard, setDashboard] = useState<Dashboard | null>(null)
  const [hostname, setHostname] = useState('')
  const [error, setError] = useState<unknown>(null)
  const [confirmHost, setConfirmHost] = useState(false)
  const [expected, setExpected] = useState<string>()

  useEffect(() => {
    Promise.all([
      client.GET('/api/v1/users'),
      client.GET('/api/v1/roles'),
      client.GET('/api/v1/audit'),
      client.GET('/api/v1/settings/dashboard-domain'),
    ])
      .then(([u, r, a, d]) => {
        setUsers(u.data?.items ?? [])
        setRoles(r.data ?? [])
        setAudit(a.data?.items ?? [])
        setDashboard(d.data ?? null)
        setHostname(d.data?.hostname ?? '')
      })
      .catch(setError)
  }, [])

  async function saveDomain() {
    setError(null)
    try {
      await client.PUT('/api/v1/settings/dashboard-domain', {
        body: { hostname, confirmHostname: hostname },
      })
    } catch (err) {
      if (err instanceof ApiError && err.confirmField === 'confirmHostname') {
        setExpected(err.expected ?? hostname)
        setConfirmHost(true)
        return
      }
      setError(err)
    }
  }

  return (
    <div className="mx-auto max-w-4xl">
      <PageHeader title="Settings" description="Users, the dashboard domain, and the audit log. One host — no organisations." />
      {error != null && <ProblemBanner error={error} />}

      <div className="flex flex-col gap-4">
        <Panel title="Dashboard domain" description="Changing this requires typing the hostname back.">
          <Field label="Hostname" htmlFor="dash">
            <TextInput id="dash" mono value={hostname} onChange={(e) => setHostname(e.target.value)} />
          </Field>
          <div className="mt-3">
            <Button onClick={() => void saveDomain()}>Save domain</Button>
          </div>
          {dashboard?.checks && dashboard.checks.length > 0 && (
            <p className="mt-2 text-xs text-muted-foreground">{dashboard.checks.length} pre-flight checks on file</p>
          )}
        </Panel>

        <Panel title="Users" bodyClassName="p-0">
          <ul className="divide-y divide-border">
            {users.map((u) => (
              <li key={u.id} className="flex items-center justify-between px-4 py-3">
                <div>
                  <p className="text-sm font-medium">{u.displayName}</p>
                  <p className="font-mono text-xs text-muted-foreground">{u.email}</p>
                </div>
                <span className="font-mono text-xs text-muted-foreground">{u.roles.join(', ')}</span>
              </li>
            ))}
          </ul>
        </Panel>

        <Panel title="Roles" description="Permissions are codes. The API authorises on those, not on role names.">
          <ul className="flex flex-col gap-3">
            {roles.map((r) => (
              <li key={r.id}>
                <p className="text-sm font-medium">{r.name}</p>
                <p className="font-mono text-[11px] text-muted-foreground">{r.permissions.join(' · ')}</p>
              </li>
            ))}
          </ul>
        </Panel>

        <Panel title="Audit log" bodyClassName="p-0">
          <ul className="divide-y divide-border">
            {audit.map((e) => (
              <li key={e.id} className="px-4 py-2.5">
                <p className="font-mono text-sm">
                  {e.action} {e.resourceSlug ?? ''}
                </p>
                <p className="font-mono text-xs text-muted-foreground">
                  {e.userEmail ?? 'system'} · {e.result} · {formatRelative(e.occurredAt)}
                </p>
              </li>
            ))}
          </ul>
        </Panel>
      </div>

      <ConfirmDialog
        open={confirmHost}
        onOpenChange={setConfirmHost}
        tone="danger"
        title="Change dashboard domain"
        confirmLabel="Change domain"
        requireTyped={expected}
        description="Type the hostname. A wrong host here locks you out of the dashboard."
        onConfirm={() => {
          void client.PUT('/api/v1/settings/dashboard-domain', {
            body: { hostname, confirmHostname: expected ?? hostname },
          })
        }}
      />
    </div>
  )
}
