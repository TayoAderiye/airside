'use client'

import { useEffect, useState } from 'react'
import { useRouter } from 'next/navigation'
import { GitBranch, Container, FileCode, Loader2, Plus, Trash2 } from 'lucide-react'

import { Panel, PageHeader } from '@/components/ui/panel'
import { Field, TextInput, NativeSelect, Slider, Hint } from '@/components/ui/field'
import { AllocationRail } from '@/components/allocation-rail'
import { BackLink } from '@/components/ui/back-link'
import { Button } from '@/components/ui/button'
import { ProblemBanner } from '@/components/problem-banner'
import { client } from '@/lib/api/client'
import { bytesToGiB, coresToNanos, cpuRail, giBToBytes, memoryRail, nanosToCores } from '@/lib/api/units'
import type { components } from '@/lib/api/schema'
import { cn } from '@/lib/utils'

type Host = components['schemas']['HostDto']
type EnvVar = { key: string; value: string; secret: boolean }

/**
 * Three, not four. The API's SourceKind is image, git, or dockerfile, and its
 * own comment says compose is out of scope — offering it here produced a form
 * that could only ever be rejected.
 */
const SOURCES = [
  { kind: 'git', label: 'Git repository', icon: GitBranch, blurb: 'Build from a branch' },
  { kind: 'image', label: 'Container image', icon: Container, blurb: 'Deploy a prebuilt image by tag' },
  { kind: 'dockerfile', label: 'Dockerfile', icon: FileCode, blurb: 'Build from an in-repo Dockerfile' },
] as const

type SourceKind = (typeof SOURCES)[number]['kind']

/**
 * The smallest application this form can express. See the database form for why
 * these are named rather than repeated at each use.
 */
const MIN_CORES = 0.25
const MIN_MEMORY_GIB = 0.5

export function AppCreateForm() {
  const router = useRouter()

  const [host, setHost] = useState<Host | null>(null)
  const [source, setSource] = useState<SourceKind>('image')
  const [name, setName] = useState('')
  const [repo, setRepo] = useState('')
  const [branch, setBranch] = useState('main')
  const [image, setImage] = useState('')
  const [dockerfilePath, setDockerfilePath] = useState('Dockerfile')
  const [port, setPort] = useState('8080')

  // Required by the API, with no "none" option, because zero-downtime cutover
  // is start-new / poll-health / swap / stop-old. Without a health check that
  // degrades to waiting a few seconds and hoping.
  const [healthKind, setHealthKind] = useState<'http' | 'command'>('http')
  const [healthPath, setHealthPath] = useState('/health')
  const [healthStatus, setHealthStatus] = useState('200')
  const [healthCommand, setHealthCommand] = useState('')

  const [cpu, setCpu] = useState(0.5)
  const [memory, setMemory] = useState(1)
  const [env, setEnv] = useState<EnvVar[]>([{ key: '', value: '', secret: false }])

  const [error, setError] = useState<unknown>(null)
  const [stage, setStage] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    client
      .GET('/api/v1/host')
      .then((res) => {
        if (cancelled) return

        const h = res.data ?? null
        setHost(h)

        // Brought inside what the host will admit, the same way the database
        // form does it. Without this the sliders ran to 4 cores and 8 GiB on
        // every host, so the defaults on a 2 GB instance produced a 409 after
        // submitting, with the overshoot visible only on the rail beside them.
        if (h) {
          setCpu((c) => Math.max(MIN_CORES, Math.min(c, nanosToCores(h.available.cpuNanos))))
          setMemory((m) => Math.max(MIN_MEMORY_GIB, Math.min(m, bytesToGiB(h.available.memoryBytes))))
        }
      })
      .catch(() => {
        // The rails are a nicety; a host that will not answer is the shell's
        // problem to report, not this form's.
      })
    return () => {
      cancelled = true
    }
  }, [])

  const availableCores = host ? nanosToCores(host.available.cpuNanos) : 4
  const availableMemory = host ? bytesToGiB(host.available.memoryBytes) : 8

  /**
   * Whether this host can admit the smallest application the form can express.
   *
   * Same reasoning as the database form: the clamp applies a floor after taking
   * the minimum, so a host with less headroom than the floor leaves the control
   * pinned to a value the API refuses. Saying so beats submitting into a 409
   * that no adjustment on this screen can avoid.
   */
  const shortfalls = host
    ? [
        availableCores < MIN_CORES ? `${MIN_CORES} cores of CPU (${availableCores.toFixed(2)} free)` : null,
        availableMemory < MIN_MEMORY_GIB
          ? `${MIN_MEMORY_GIB} GiB of memory (${availableMemory.toFixed(2)} free)`
          : null,
      ].filter((x): x is string => x !== null)
    : []

  const sourceValid =
    (source === 'git' && repo.trim()) ||
    (source === 'image' && image.trim()) ||
    (source === 'dockerfile' && repo.trim())

  const healthValid = healthKind === 'http' ? healthPath.trim().startsWith('/') : healthCommand.trim().length > 0

  const canSubmit =
    shortfalls.length === 0 &&
    name.trim().length > 1 &&
    Boolean(sourceValid) &&
    Number(port) > 0 &&
    healthValid &&
    !stage

  function updateEnv(i: number, patch: Partial<EnvVar>) {
    setEnv((prev) => prev.map((e, idx) => (idx === i ? { ...e, ...patch } : e)))
  }

  async function submit() {
    if (!canSubmit) return
    setError(null)

    try {
      // Three calls, because the API separates them. Creating an application is
      // synchronous and returns the application; deploying it is the job. The
      // screen this replaced conflated the two and did neither.
      setStage('Creating the application…')

      const created = await client.POST('/api/v1/applications', {
        body: {
          slug: name,
          displayName: name,
          cpuNanos: coresToNanos(cpu),
          memoryBytes: giBToBytes(memory),
          containerPort: Number(port),
          sourceKind: source,
          ...(source === 'image' ? { imageRef: image } : {}),
          ...(source === 'git' ? { gitRepositoryUrl: repo, gitBranch: branch } : {}),
          ...(source === 'dockerfile' ? { gitRepositoryUrl: repo, gitBranch: branch, dockerfilePath } : {}),
          // Every field named, including the nulls. The generated type requires
          // them, and being explicit about "no command" beats relying on an
          // omission to mean the same thing.
          healthCheck: {
            kind: healthKind,
            path: healthKind === 'http' ? healthPath : null,
            expectedStatus: healthKind === 'http' ? Number(healthStatus) || 200 : null,

            // An argument vector, never a command line — there is no shell.
            command: healthKind === 'command' ? healthCommand.trim().split(/\s+/) : null,
            intervalSeconds: 10,
            timeoutSeconds: 5,
            retries: 3,
          },
        },
      })

      const appId = created.data?.id
      if (!appId) throw new Error('The application was created without an id.')

      const pairs = env.filter((e) => e.key.trim())

      if (pairs.length > 0) {
        setStage('Setting environment variables…')

        // One call each: the API keys environment by name so it can audit a
        // single variable changing, which a bulk replace could not.
        for (const pair of pairs) {
          await client.PUT('/api/v1/applications/{id}/environment/{key}', {
            params: { path: { id: appId, key: pair.key } },
            body: { value: pair.value, isSecret: pair.secret },
          })
        }
      }

      setStage('Starting the deployment…')

      const deployed = await client.POST('/api/v1/applications/{id}/deployments', {
        params: { path: { id: appId } },
        body: {
          branch: source === 'image' ? null : branch,
          commitSha: null,
          imageRef: source === 'image' ? image : null,
        },
      })

      if (deployed.data?.jobId) {
        router.push(`/applications/new/deploying?job=${deployed.data.jobId}&app=${appId}`)
        return
      }

      // Created but not deploying: the application exists and saying so is
      // better than implying nothing happened.
      router.push(`/applications/${appId}`)
    } catch (err) {
      setError(err)
      setStage(null)
    }
  }

  return (
    <div className="flex flex-col gap-5">
      <BackLink href="/applications">Applications</BackLink>
      <PageHeader title="Deploy application" description="Define a workload and its resource envelope on this host." />

      <div className="grid grid-cols-1 gap-5 lg:grid-cols-[1fr_320px]">
        <div className="flex flex-col gap-5">
          <Panel title="Source" description="Where this application's code or image comes from.">
            <div className="grid grid-cols-1 gap-2 sm:grid-cols-3">
              {SOURCES.map((s) => {
                const Icon = s.icon
                const active = source === s.kind
                return (
                  <button
                    key={s.kind}
                    type="button"
                    onClick={() => setSource(s.kind)}
                    aria-pressed={active}
                    className={cn(
                      'flex items-start gap-2.5 rounded-md border p-3 text-left transition-colors',
                      active ? 'border-ring bg-accent/10' : 'border-border hover:border-ring/50',
                    )}
                  >
                    <Icon className={cn('mt-0.5 size-4 shrink-0', active ? 'text-accent' : 'text-muted-foreground')} />
                    <span className="min-w-0">
                      <span className="block text-sm font-medium text-foreground">{s.label}</span>
                      <span className="block text-xs text-muted-foreground">{s.blurb}</span>
                    </span>
                  </button>
                )
              })}
            </div>

            <div className="mt-4 flex flex-col gap-4">
              {source === 'image' && (
                <Field label="Image reference" htmlFor="image" required hint="registry/image:tag">
                  <TextInput
                    id="image"
                    value={image}
                    onChange={(e) => setImage(e.target.value)}
                    placeholder="ghcr.io/acme/worker:1.4.2"
                  />
                </Field>
              )}
              {(source === 'git' || source === 'dockerfile') && (
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-[1fr_160px]">
                  <Field label="Repository" htmlFor="repo" required hint="Full clone URL">
                    <TextInput
                      id="repo"
                      value={repo}
                      onChange={(e) => setRepo(e.target.value)}
                      placeholder="https://github.com/acme/api-gateway.git"
                    />
                  </Field>
                  <Field label="Branch" htmlFor="branch">
                    <TextInput id="branch" value={branch} onChange={(e) => setBranch(e.target.value)} />
                  </Field>
                </div>
              )}
              {source === 'dockerfile' && (
                <Field label="Dockerfile path" htmlFor="df" hint="Relative to the repository root.">
                  <TextInput id="df" value={dockerfilePath} onChange={(e) => setDockerfilePath(e.target.value)} />
                </Field>
              )}
            </div>
          </Panel>

          <Panel title="Identity & networking">
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
              <Field label="Application name" htmlFor="name" required hint="Lowercase, used for the internal DNS name.">
                <TextInput
                  id="name"
                  value={name}
                  onChange={(e) => setName(e.target.value.toLowerCase())}
                  placeholder="api-gateway"
                />
              </Field>
              <Field label="Container port" htmlFor="port" required hint="Port the process listens on inside the container.">
                <TextInput
                  id="port"
                  inputMode="numeric"
                  value={port}
                  onChange={(e) => setPort(e.target.value.replace(/[^0-9]/g, ''))}
                  placeholder="8080"
                />
              </Field>
            </div>
          </Panel>

          <Panel
            title="Health check"
            description="Required. The new container has to pass this before traffic moves and the old one stops."
          >
            <Field label="Kind" htmlFor="health-kind">
              <NativeSelect
                id="health-kind"
                value={healthKind}
                onChange={(e) => setHealthKind(e.target.value as 'http' | 'command')}
              >
                <option value="http">HTTP — request a path and check the status</option>
                <option value="command">Command — run it in the container, exit 0 is healthy</option>
              </NativeSelect>
            </Field>

            {healthKind === 'http' ? (
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-[1fr_160px]">
                <Field label="Path" htmlFor="health-path" required>
                  <TextInput id="health-path" value={healthPath} onChange={(e) => setHealthPath(e.target.value)} />
                </Field>
                <Field label="Expected status" htmlFor="health-status">
                  <TextInput
                    id="health-status"
                    inputMode="numeric"
                    value={healthStatus}
                    onChange={(e) => setHealthStatus(e.target.value.replace(/[^0-9]/g, ''))}
                  />
                </Field>
              </div>
            ) : (
              <Field label="Command" htmlFor="health-cmd" required>
                <TextInput
                  id="health-cmd"
                  value={healthCommand}
                  onChange={(e) => setHealthCommand(e.target.value)}
                  placeholder="pg_isready -U app"
                  className="font-mono"
                />
                <Hint>Split on spaces into an argument vector. There is no shell, so pipes and redirection will not work.</Hint>
              </Field>
            )}
          </Panel>

          <Panel title="Resources" description="Limits are reserved from host capacity as soon as the application is created.">
            <div className="grid grid-cols-1 gap-5 sm:grid-cols-2">
              <Field label={`CPU — ${cpu.toFixed(2)} cores`} htmlFor="cpu">
                <Slider
                  id="cpu"
                  value={cpu}
                  onChange={setCpu}
                  min={MIN_CORES}
                  max={Math.max(MIN_CORES, Math.min(4, availableCores))}
                  step={0.25}
                />
              </Field>
              <Field label={`Memory — ${memory} GiB`} htmlFor="mem">
                <Slider
                  id="mem"
                  value={memory}
                  onChange={setMemory}
                  min={MIN_MEMORY_GIB}
                  max={Math.max(MIN_MEMORY_GIB, Math.min(8, availableMemory))}
                  step={0.5}
                />
              </Field>
            </div>
          </Panel>

          <Panel
            title="Environment"
            description="Injected at container start. Secrets are encrypted at rest and masked in every response."
            actions={
              <Button
                variant="outline"
                size="sm"
                onClick={() => setEnv((prev) => [...prev, { key: '', value: '', secret: false }])}
              >
                <Plus className="size-3.5" /> Add
              </Button>
            }
          >
            <div className="flex flex-col gap-2">
              {env.map((e, i) => (
                <div key={i} className="flex items-center gap-2">
                  <TextInput
                    aria-label="Key"
                    value={e.key}
                    onChange={(ev) => updateEnv(i, { key: ev.target.value.toUpperCase() })}
                    placeholder="KEY"
                    className="font-mono"
                  />
                  <TextInput
                    aria-label="Value"
                    type={e.secret ? 'password' : 'text'}
                    value={e.value}
                    onChange={(ev) => updateEnv(i, { value: ev.target.value })}
                    placeholder="value"
                    className="font-mono"
                  />
                  <button
                    type="button"
                    onClick={() => updateEnv(i, { secret: !e.secret })}
                    aria-pressed={e.secret}
                    className={cn(
                      'shrink-0 rounded-md border px-2 py-2 text-xs transition-colors',
                      e.secret
                        ? 'border-transparent bg-primary/80 text-primary-foreground'
                        : 'border-border text-muted-foreground hover:text-foreground',
                    )}
                    title="Toggle secret"
                  >
                    secret
                  </button>
                  <button
                    type="button"
                    onClick={() => setEnv((prev) => prev.filter((_, idx) => idx !== i))}
                    className="shrink-0 rounded-md border border-border p-2 text-muted-foreground transition-colors hover:border-failed/50 hover:text-failed"
                    aria-label="Remove variable"
                  >
                    <Trash2 className="size-4" />
                  </button>
                </div>
              ))}
            </div>
          </Panel>

          {error != null && <ProblemBanner error={error} />}
        </div>

        <aside className="flex flex-col gap-4 lg:sticky lg:top-4 lg:self-start">
          {host && (
            <Panel title="Host impact" description="Where this workload's request lands on the host.">
              <div className="flex flex-col gap-4">
                <AllocationRail
                  label="CPU"
                  triple={cpuRail(host.capacity, host.allocated, host.used)}
                  requested={cpuRail(host.capacity, host.allocated, host.used).allocated + cpu}
                />
                <AllocationRail
                  label="Memory"
                  triple={memoryRail(host.capacity, host.allocated, host.used)}
                  requested={memoryRail(host.capacity, host.allocated, host.used).allocated + memory}
                />
              </div>
            </Panel>
          )}
          <Button className="w-full" disabled={!canSubmit} onClick={submit}>
            {stage ? (
              <>
                <Loader2 className="size-3.5 animate-spin" /> {stage}
              </>
            ) : (
              'Deploy application'
            )}
          </Button>
          {!canSubmit && !stage && (
            <p className="text-center text-xs text-muted-foreground">
              {shortfalls.length > 0
                ? `This host cannot fit another workload — short of ${shortfalls.join(', and ')}. Free space or move to a larger instance.`
                : 'Name, source, port and health check are required.'}
            </p>
          )}
        </aside>
      </div>
    </div>
  )
}
