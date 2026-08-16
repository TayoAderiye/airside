'use client'

import { useEffect, useState } from 'react'

import { JobWatcher } from '@/components/job-watcher'
import { PreflightList } from '@/components/preflight-list'
import { ProblemBanner } from '@/components/problem-banner'
import { WarningsList } from '@/components/warnings-list'
import { Button } from '@/components/ui/button'
import { Field, TextInput } from '@/components/ui/field'
import { PageHeader, Panel } from '@/components/ui/panel'
import { client } from '@/lib/api/client'
import type { JobAccepted } from '@/lib/api/jobs'
import type { components } from '@/lib/api/schema'
import { cn } from '@/lib/utils'

type App = components['schemas']['ApplicationSummaryDto']
type Domain = components['schemas']['DomainDto']
type TlsMode = components['schemas']['TlsModeDto']
type Report = components['schemas']['PreflightReportDto']

export function DomainsView() {
  const [apps, setApps] = useState<App[]>([])
  const [appId, setAppId] = useState<string>()
  const [domains, setDomains] = useState<Domain[]>([])
  const [modes, setModes] = useState<TlsMode[]>([])
  const [hostname, setHostname] = useState('')
  const [tlsMode, setTlsMode] = useState<string>('')
  const [preflight, setPreflight] = useState<Report | null>(null)
  const [job, setJob] = useState<JobAccepted | null>(null)
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    Promise.all([client.GET('/api/v1/applications'), client.GET('/api/v1/tls-modes')])
      .then(([a, m]) => {
        const items = a.data?.items ?? []
        setApps(items)
        setAppId(items[0]?.id)
        setModes(m.data ?? [])
      })
      .catch(setError)
  }, [])

  useEffect(() => {
    if (!appId) return
    client
      .GET('/api/v1/applications/{id}/domains', { params: { path: { id: appId } } })
      .then((r) => setDomains(r.data ?? []))
      .catch(setError)
  }, [appId])

  async function runPreflight() {
    setError(null)
    try {
      const res = await client.POST('/api/v1/domains/preflight', {
        body: { hostname, tlsMode, skipPreflight: false },
      })
      setPreflight(res.data ?? null)
    } catch (err) {
      setError(err)
    }
  }

  async function addDomain() {
    if (!appId || !tlsMode) return
    setError(null)
    try {
      const res = await client.POST('/api/v1/applications/{id}/domains', {
        params: { path: { id: appId } },
        body: { hostname, tlsMode, skipPreflight: false, redirectToDomainId: null },
      })
      if (res.response.status === 202 && res.data) setJob(res.data)
    } catch (err) {
      setError(err)
    }
  }

  return (
    <div className="flex flex-col gap-5">
      <PageHeader title="Domains" description="TLS mode is required and has no default. Pre-flight is the check, not a validation error." />
      {error != null && <ProblemBanner error={error} />}

      <Field label="Application" htmlFor="app">
        <select
          id="app"
          className="w-full rounded-md border border-input bg-background px-3 py-1.5 text-sm"
          value={appId ?? ''}
          onChange={(e) => setAppId(e.target.value)}
        >
          {apps.map((a) => (
            <option key={a.id} value={a.id}>
              {a.slug}
            </option>
          ))}
        </select>
      </Field>

      <Panel title="Add domain">
        <div className="flex flex-col gap-4">
          <Field label="Hostname" htmlFor="host" required>
            <TextInput id="host" mono value={hostname} onChange={(e) => setHostname(e.target.value)} />
          </Field>
          <fieldset>
            <legend className="mb-2 text-sm font-medium">TLS mode</legend>
            <div className="flex flex-col gap-2">
              {modes.map((m) => (
                <label
                  key={m.value}
                  className={cn(
                    'flex cursor-pointer items-start gap-3 rounded-md border p-3',
                    tlsMode === m.value ? 'border-primary bg-primary/5' : 'border-border',
                    !m.available && 'opacity-50',
                  )}
                >
                  <input
                    type="radio"
                    name="tls"
                    value={m.value}
                    disabled={!m.available}
                    checked={tlsMode === m.value}
                    onChange={() => setTlsMode(m.value)}
                    className="mt-1"
                  />
                  <span>
                    <span className="block text-sm font-medium">{m.label}</span>
                    <span className="block text-xs text-muted-foreground">{m.summary}</span>
                    {!m.available && <span className="block text-xs text-degraded">Not available on this host</span>}
                  </span>
                </label>
              ))}
            </div>
          </fieldset>
          <div className="flex gap-2">
            <Button variant="outline" onClick={() => void runPreflight()} disabled={!hostname || !tlsMode}>
              Pre-flight
            </Button>
            <Button onClick={() => void addDomain()} disabled={!hostname || !tlsMode || (preflight?.blocks ?? false)}>
              Add domain
            </Button>
          </div>
          {preflight && <PreflightList report={preflight} />}
        </div>
      </Panel>

      {job && <JobWatcher job={job} />}

      <Panel title="Attached" bodyClassName="p-0">
        <ul className="divide-y divide-border">
          {domains.map((d) => (
            <li key={d.id} className="px-4 py-3">
              <p className="font-mono text-sm">{d.hostname}</p>
              <p className="text-xs text-muted-foreground">
                TLS {d.tlsMode} · {d.status}
              </p>
              <div className="mt-2">
                <WarningsList warnings={d.warnings} />
              </div>
            </li>
          ))}
          {domains.length === 0 && <p className="px-4 py-6 text-sm text-muted-foreground">No domains on this application.</p>}
        </ul>
      </Panel>
    </div>
  )
}
