'use client'

import { useEffect, useMemo, useState } from 'react'
import { useRouter } from 'next/navigation'
import { Info, Loader2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Field, TextInput, NativeSelect, Toggle, Slider, Hint } from '@/components/ui/field'
import { Panel } from '@/components/ui/panel'
import { AllocationRail } from '@/components/allocation-rail'
import { EngineGlyph, engineLabel } from '@/components/engine'
import { ProblemBanner } from '@/components/problem-banner'
import { client } from '@/lib/api/client'
import { coresToNanos, giBToBytes, memoryRail } from '@/lib/api/units'
import type { components } from '@/lib/api/schema'
import type { DatabaseEngine, MaxMemoryPolicy } from '@/lib/api/types'

type Host = components['schemas']['HostDto']
type Engine = components['schemas']['DatabaseEngineDto']

export function DatabaseCreateForm() {
  const router = useRouter()

  // The engine catalogue comes from the API. It was a hardcoded map, which is a
  // promise the form cannot keep: it offered versions this build may not
  // support, and the rejection arrived only after submitting.
  const [engines, setEngines] = useState<Engine[] | null>(null)
  const [host, setHost] = useState<Host | null>(null)

  const [engineKind, setEngineKind] = useState<string>('postgres')
  const [name, setName] = useState('')
  const [version, setVersion] = useState('')
  const [cpu, setCpu] = useState(1)
  const [memory, setMemory] = useState(2)
  const [storage, setStorage] = useState(20)

  // Required by every engine that has the concept, and rejected outright by
  // those that do not — Redis exposes sixteen numbered logical databases chosen
  // at connect time, so a name there is a validation error rather than an
  // ignored field. The engine catalogue says which is which.
  const [databaseName, setDatabaseName] = useState('')
  const [username, setUsername] = useState('app')

  const [maxMemory, setMaxMemory] = useState(1.5)
  const [policy, setPolicy] = useState<MaxMemoryPolicy>('allkeys-lru')
  const [aof, setAof] = useState(true)

  const [error, setError] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    let cancelled = false

    Promise.all([client.GET('/api/v1/database-engines'), client.GET('/api/v1/host')])
      .then(([engineRes, hostRes]) => {
        if (cancelled) return

        const list = engineRes.data ?? []
        setEngines(list)
        setHost(hostRes.data ?? null)

        const first = list.find((e) => e.kind === 'postgres') ?? list[0]
        if (first) {
          setEngineKind(first.kind)
          setVersion(first.defaultVersion)
        }
      })
      .catch((err) => {
        if (!cancelled) setError(err)
      })

    return () => {
      cancelled = true
    }
  }, [])

  const engine = engines?.find((e) => e.kind === engineKind) ?? null
  const isRedis = engineKind === 'redis'

  function selectEngine(kind: string) {
    setEngineKind(kind)
    setVersion(engines?.find((e) => e.kind === kind)?.defaultVersion ?? '')
    if (kind === 'redis') setStorage((s) => Math.min(s, 10))
  }

  const nameError = useMemo(() => {
    if (!name) return null
    if (!/^[a-z][a-z0-9-]{1,38}[a-z0-9]$/.test(name))
      return 'Lowercase letters, digits and hyphens; must start with a letter.'
    return null
  }, [name])

  const caps = engine?.capabilities
  const needsDatabaseName = caps?.supportsDatabaseName ?? false
  const needsUsername = caps?.supportsUserAccounts ?? false

  /**
   * A default derived from the slug, not left blank.
   *
   * Identifiers cannot carry hyphens without quoting in Postgres or MySQL, so a
   * slug of `payments-primary` becomes `payments_primary`. The operator can
   * change it; what matters is that the obvious path does not fail validation.
   */
  const suggestedDatabaseName = name.replace(/-/g, '_')
  const effectiveDatabaseName = databaseName.trim() || suggestedDatabaseName

  const canSubmit =
    name.length > 1 &&
    !nameError &&
    !!engine &&
    !!version &&
    (!needsDatabaseName || effectiveDatabaseName.length > 0) &&
    (!needsUsername || username.trim().length > 0) &&
    !busy

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    if (!canSubmit) return

    setBusy(true)
    setError(null)

    try {
      const res = await client.POST('/api/v1/databases', {
        body: {
          slug: name,
          displayName: name,
          engine: engineKind,
          version,
          cpuNanos: coresToNanos(cpu),
          memoryBytes: giBToBytes(memory),

          // Sent for every engine, Redis included. Redis holds its data in
          // memory but the append-only file is on disk and is on by default, so
          // a zero allocation would be a zero-byte volume — harmless where
          // storage is only accounted, and an immediate failure on a host that
          // enforces quotas.
          storageBytes: giBToBytes(storage),

          // Sent only where the engine has the concept. Redis rejects both
          // outright — it authenticates with a password and has no named
          // database — so sending them anyway turns a valid request into a
          // validation error.
          ...(needsDatabaseName ? { databaseName: effectiveDatabaseName } : {}),
          ...(needsUsername ? { username: username.trim() } : {}),
          ...(isRedis
            ? {
                maxMemoryBytes: giBToBytes(maxMemory),
                maxMemoryPolicy: policy,
                aofEnabled: aof,
              }
            : {}),
        },
      })

      // 202 with a job, not a finished database. The next screen follows it.
      if (res.data?.jobId) {
        router.push(`/databases/new/provisioning?job=${res.data.jobId}`)
        return
      }

      throw new Error('The API accepted the request without returning a job.')
    } catch (err) {
      setError(err)
      setBusy(false)
    }
  }

  if (!engines) {
    return (
      <div className="flex items-center gap-2 text-sm text-muted-foreground">
        <Loader2 className="size-4 animate-spin text-transitional" />
        Loading engines…
      </div>
    )
  }

  const policies = engine?.maxMemoryPolicies ?? []

  return (
    <form className="grid grid-cols-1 gap-6 xl:grid-cols-[minmax(0,1fr)_20rem]" onSubmit={submit}>
      <div className="flex flex-col gap-6">
        <Panel title="Engine">
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
            {engines.map((e) => (
              <button
                key={e.kind}
                type="button"
                onClick={() => selectEngine(e.kind)}
                aria-pressed={engineKind === e.kind}
                className={
                  'flex flex-col items-center gap-2 rounded-lg border p-3 text-sm transition-colors ' +
                  (engineKind === e.kind
                    ? 'border-ring bg-accent text-foreground'
                    : 'border-border bg-card text-muted-foreground hover:border-ring/50')
                }
              >
                <EngineGlyph engine={e.kind as DatabaseEngine} />
                {e.displayName}
              </button>
            ))}
          </div>
        </Panel>

        <Panel title="Identity">
          <Field label="Name" htmlFor="db-name" error={nameError} required>
            <TextInput
              id="db-name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="payments-primary"
              autoComplete="off"
              spellCheck={false}
            />
          </Field>
          <Field label="Version" htmlFor="db-version">
            <NativeSelect id="db-version" value={version} onChange={(e) => setVersion(e.target.value)}>
              {(engine?.supportedVersions ?? []).map((v) => (
                <option key={v} value={v}>
                  {engine?.displayName} {v}
                </option>
              ))}
            </NativeSelect>
          </Field>

          {needsDatabaseName && (
            <Field
              label="Database name"
              htmlFor="db-dbname"
              required
              hint="Defaults to the slug with hyphens replaced, which identifiers cannot carry unquoted."
            >
              <TextInput
                id="db-dbname"
                value={databaseName}
                onChange={(e) => setDatabaseName(e.target.value)}
                placeholder={suggestedDatabaseName || 'payments_primary'}
                autoComplete="off"
                spellCheck={false}
              />
            </Field>
          )}

          {needsUsername && (
            <Field label="Username" htmlFor="db-user" required hint="The account the application connects as.">
              <TextInput
                id="db-user"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                autoComplete="off"
                spellCheck={false}
              />
            </Field>
          )}

          {!needsDatabaseName && engine && (
            <Hint>
              {engine.displayName} has no named database — it exposes numbered logical databases chosen at connect
              time. The password is generated and stored encrypted.
            </Hint>
          )}
        </Panel>

        <Panel title="Resources" description="Limits are reserved from host capacity as soon as the database is created.">
          <Field label={`CPU — ${cpu} core${cpu === 1 ? '' : 's'}`} htmlFor="db-cpu">
            <Slider id="db-cpu" min={0.5} max={8} step={0.5} value={cpu} onChange={setCpu} />
          </Field>
          <Field label={`Memory — ${memory} GiB`} htmlFor="db-mem">
            <Slider id="db-mem" min={0.5} max={32} step={0.5} value={memory} onChange={setMemory} />
          </Field>
          <Field
            label={`Storage — ${storage} GiB`}
            htmlFor="db-storage"
            hint={isRedis ? 'Redis keeps data in memory, but the append-only file is written here.' : undefined}
          >
            <Slider id="db-storage" min={isRedis ? 1 : 5} max={500} step={isRedis ? 1 : 5} value={storage} onChange={setStorage} />
          </Field>
        </Panel>

        {isRedis && (
          <Panel
            title="Redis configuration"
            description="Redis is memory-first: how it behaves when memory fills is a deliberate choice, not a default."
          >
            <Field
              label={`Max memory — ${maxMemory} GiB`}
              htmlFor="db-maxmem"
              hint={maxMemory >= memory ? 'At or above the memory limit — the container cap will apply first.' : undefined}
            >
              <Slider id="db-maxmem" min={0.25} max={Math.max(0.5, memory)} step={0.25} value={maxMemory} onChange={setMaxMemory} />
            </Field>
            <Field label="Eviction policy" htmlFor="db-policy">
              <NativeSelect id="db-policy" value={policy} onChange={(e) => setPolicy(e.target.value as MaxMemoryPolicy)}>
                {policies.map((p) => (
                  <option key={p} value={p}>
                    {p}
                  </option>
                ))}
              </NativeSelect>
              {policy === 'noeviction' && (
                <Hint tone="warn">
                  With noeviction, writes fail once memory is full. Use only when losing data is worse than rejecting writes.
                </Hint>
              )}
            </Field>
            <Toggle
              label="Append-only file (AOF)"
              description="Durable on-disk log. Safer across restarts, slightly slower writes."
              checked={aof}
              onChange={setAof}
            />
          </Panel>
        )}

        {error != null && <ProblemBanner error={error} />}
      </div>

      <aside className="flex flex-col gap-4 xl:sticky xl:top-6 xl:self-start">
        {host && (
          <Panel title="Host impact">
            <p className="mb-3 flex items-start gap-2 text-xs text-muted-foreground">
              <Info className="mt-0.5 size-3.5 shrink-0" />
              The marker shows where host memory allocation lands after this database is created.
            </p>
            <AllocationRail
              label="Host memory"
              triple={memoryRail(host.capacity, host.allocated, host.used)}
              requested={memoryRail(host.capacity, host.allocated, host.used).allocated + memory}
            />
          </Panel>
        )}
        <Panel title="Summary">
          <dl className="flex flex-col gap-2 font-mono text-xs">
            <SummaryRow k="Engine" v={`${engine?.displayName ?? engineKind} ${version}`} />
            <SummaryRow k="CPU" v={`${cpu} cores`} />
            <SummaryRow k="Memory" v={`${memory} GiB`} />
            <SummaryRow k="Storage" v={`${storage} GiB`} />
            {isRedis && <SummaryRow k="Max memory" v={`${maxMemory} GiB`} />}
            {isRedis && <SummaryRow k="Policy" v={policy} />}
            {isRedis && <SummaryRow k="AOF" v={aof ? 'enabled' : 'disabled'} />}
          </dl>
        </Panel>
        <Button type="submit" disabled={!canSubmit} className="w-full">
          {busy ? 'Creating…' : 'Create database'}
        </Button>
      </aside>
    </form>
  )
}

function SummaryRow({ k, v }: { k: string; v: string }) {
  return (
    <div className="flex items-center justify-between gap-2">
      <dt className="text-muted-foreground">{k}</dt>
      <dd className="text-foreground">{v}</dd>
    </div>
  )
}
