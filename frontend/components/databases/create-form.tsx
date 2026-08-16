'use client'

import { useMemo, useState } from 'react'
import { useRouter } from 'next/navigation'
import { Info } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Field, TextInput, NativeSelect, Toggle, Slider, Hint } from '@/components/ui/field'
import { Panel } from '@/components/ui/panel'
import { AllocationRail } from '@/components/allocation-rail'
import { EngineGlyph, engineLabel } from '@/components/engine'
import { hostHealth } from '@/lib/api/mock'
import type { DatabaseEngine, MaxMemoryPolicy } from '@/lib/api/types'

const ENGINES: DatabaseEngine[] = ['postgres', 'mysql', 'mongodb', 'redis']

const VERSIONS: Record<DatabaseEngine, string[]> = {
  postgres: ['16', '15', '14'],
  mysql: ['8.4', '8.0'],
  mongodb: ['7.0', '6.0'],
  redis: ['7.4', '7.2'],
}

const POLICIES: { value: MaxMemoryPolicy; label: string }[] = [
  { value: 'noeviction', label: 'noeviction — reject writes when full' },
  { value: 'allkeys-lru', label: 'allkeys-lru — evict least-recently-used' },
  { value: 'allkeys-lfu', label: 'allkeys-lfu — evict least-frequently-used' },
  { value: 'volatile-lru', label: 'volatile-lru — LRU among keys with TTL' },
  { value: 'volatile-ttl', label: 'volatile-ttl — shortest TTL first' },
  { value: 'allkeys-random', label: 'allkeys-random' },
]

export function DatabaseCreateForm() {
  const router = useRouter()
  const [engine, setEngine] = useState<DatabaseEngine>('postgres')
  const [name, setName] = useState('')
  const [version, setVersion] = useState(VERSIONS.postgres[0])
  const [cpu, setCpu] = useState(1)
  const [memory, setMemory] = useState(2)
  const [storage, setStorage] = useState(20)

  // Redis-specific
  const [maxMemory, setMaxMemory] = useState(1.5)
  const [policy, setPolicy] = useState<MaxMemoryPolicy>('allkeys-lru')
  const [aof, setAof] = useState(true)

  const isRedis = engine === 'redis'

  function selectEngine(e: DatabaseEngine) {
    setEngine(e)
    setVersion(VERSIONS[e][0])
    if (e === 'redis') setStorage((s) => Math.min(s, 10))
  }

  const nameError = useMemo(() => {
    if (!name) return null
    if (!/^[a-z][a-z0-9-]{1,38}[a-z0-9]$/.test(name))
      return 'Lowercase letters, digits and hyphens; must start with a letter.'
    return null
  }, [name])

  const canSubmit = name.length > 1 && !nameError

  // Preview the effect on host memory allocation
  const memPreview = hostHealth.memory.allocated + memory

  return (
    <form
      className="grid grid-cols-1 gap-6 xl:grid-cols-[minmax(0,1fr)_20rem]"
      onSubmit={(e) => {
        e.preventDefault()
        if (!canSubmit) return
        router.push(`/databases/new/provisioning?name=${encodeURIComponent(name)}&engine=${engine}`)
      }}
    >
      <div className="flex flex-col gap-6">
        <Panel title="Engine">
          <div className="grid grid-cols-2 gap-2 sm:grid-cols-4">
            {ENGINES.map((e) => (
              <button
                key={e}
                type="button"
                onClick={() => selectEngine(e)}
                aria-pressed={engine === e}
                className={
                  'flex flex-col items-center gap-2 rounded-lg border p-3 text-sm transition-colors ' +
                  (engine === e
                    ? 'border-ring bg-accent text-foreground'
                    : 'border-border bg-card text-muted-foreground hover:border-ring/50')
                }
              >
                <EngineGlyph engine={e} />
                {engineLabel(e)}
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
              {VERSIONS[engine].map((v) => (
                <option key={v} value={v}>
                  {engineLabel(engine)} {v}
                </option>
              ))}
            </NativeSelect>
          </Field>
        </Panel>

        <Panel title="Resources" description="Limits are reserved from host capacity as soon as the database is created.">
          <Field label={`CPU — ${cpu} core${cpu === 1 ? '' : 's'}`} htmlFor="db-cpu">
            <Slider id="db-cpu" min={0.5} max={8} step={0.5} value={cpu} onChange={setCpu} />
          </Field>
          <Field label={`Memory — ${memory} GiB`} htmlFor="db-mem">
            <Slider id="db-mem" min={0.5} max={32} step={0.5} value={memory} onChange={setMemory} />
          </Field>
          {!isRedis && (
            <Field label={`Storage — ${storage} GiB`} htmlFor="db-storage">
              <Slider id="db-storage" min={5} max={500} step={5} value={storage} onChange={setStorage} />
            </Field>
          )}
        </Panel>

        {/* Engine-dependent section — Redis exposes memory semantics the others don't. */}
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
                {POLICIES.map((p) => (
                  <option key={p.value} value={p.value}>
                    {p.label}
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
      </div>

      {/* Live impact sidebar */}
      <aside className="flex flex-col gap-4 xl:sticky xl:top-6 xl:self-start">
        <Panel title="Host impact">
          <p className="mb-3 flex items-start gap-2 text-xs text-muted-foreground">
            <Info className="mt-0.5 size-3.5 shrink-0" />
            The marker shows where host memory allocation lands after this database is created.
          </p>
          <AllocationRail
            label="Host memory"
            triple={hostHealth.memory}
            requested={memPreview}
          />
        </Panel>
        <Panel title="Summary">
          <dl className="flex flex-col gap-2 font-mono text-xs">
            <SummaryRow k="Engine" v={`${engineLabel(engine)} ${version}`} />
            <SummaryRow k="CPU" v={`${cpu} cores`} />
            <SummaryRow k="Memory" v={`${memory} GiB`} />
            {!isRedis && <SummaryRow k="Storage" v={`${storage} GiB`} />}
            {isRedis && <SummaryRow k="Max memory" v={`${maxMemory} GiB`} />}
            {isRedis && <SummaryRow k="Policy" v={policy} />}
            {isRedis && <SummaryRow k="AOF" v={aof ? 'enabled' : 'disabled'} />}
          </dl>
        </Panel>
        <Button type="submit" disabled={!canSubmit} className="w-full">
          Create database
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
