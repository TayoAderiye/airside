'use client'

import { useMemo, useState } from 'react'
import { useRouter } from 'next/navigation'
import { GitBranch, Container, FileCode, Layers, Plus, Trash2 } from 'lucide-react'
import type { AppSourceKind } from '@/lib/api/types'
import { hostHealth } from '@/lib/api/mock'
import { Panel, PageHeader } from '@/components/ui/panel'
import { Field, TextInput, Textarea, NativeSelect, Slider, Hint } from '@/components/ui/field'
import { AllocationRail } from '@/components/allocation-rail'
import { BackLink } from '@/components/ui/back-link'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'

type EnvVar = { key: string; value: string; secret: boolean }

const SOURCES: { kind: AppSourceKind; label: string; icon: typeof GitBranch; blurb: string }[] = [
  { kind: 'git', label: 'Git repository', icon: GitBranch, blurb: 'Build from a branch on push' },
  { kind: 'image', label: 'Container image', icon: Container, blurb: 'Deploy a prebuilt image by tag' },
  { kind: 'dockerfile', label: 'Dockerfile', icon: FileCode, blurb: 'Build from an in-repo Dockerfile' },
  { kind: 'compose', label: 'Compose', icon: Layers, blurb: 'Multi-service compose stack' },
]

export function AppCreateForm() {
  const router = useRouter()
  const [source, setSource] = useState<AppSourceKind>('git')
  const [name, setName] = useState('')
  const [repo, setRepo] = useState('')
  const [branch, setBranch] = useState('main')
  const [image, setImage] = useState('')
  const [dockerfilePath, setDockerfilePath] = useState('Dockerfile')
  const [composePath, setComposePath] = useState('docker-compose.yml')
  const [port, setPort] = useState('3000')
  const [replicas, setReplicas] = useState(2)
  const [cpu, setCpu] = useState(0.5)
  const [memory, setMemory] = useState(1)
  const [env, setEnv] = useState<EnvVar[]>([{ key: '', value: '', secret: false }])
  const [submitting, setSubmitting] = useState(false)

  // Aggregate request = per-replica limit × replicas. This is the allocation
  // that actually gets promised to the scheduler, so preview it that way.
  const cpuReq = cpu * replicas
  const memReq = memory * replicas

  const cpuTriple = useMemo(() => hostHealth.cpu, [])

  const sourceValid =
    (source === 'git' && repo.trim()) ||
    (source === 'image' && image.trim()) ||
    source === 'dockerfile' ||
    source === 'compose'
  const canSubmit = name.trim().length > 1 && Boolean(sourceValid) && Number(port) > 0

  function updateEnv(i: number, patch: Partial<EnvVar>) {
    setEnv((prev) => prev.map((e, idx) => (idx === i ? { ...e, ...patch } : e)))
  }

  function submit() {
    setSubmitting(true)
    const params = new URLSearchParams({ name, kind: 'app.deploy' })
    setTimeout(() => router.push(`/applications/new/deploying?${params.toString()}`), 400)
  }

  return (
    <div className="flex flex-col gap-5">
      <BackLink href="/applications">Applications</BackLink>
      <PageHeader title="Deploy application" description="Define a workload and its resource envelope on this host." />

      <div className="grid grid-cols-1 gap-5 lg:grid-cols-[1fr_320px]">
        <div className="flex flex-col gap-5">
          {/* Source — the fields below adapt to this choice */}
          <Panel title="Source" description="Where this application's code or image comes from.">
            <div className="grid grid-cols-2 gap-2">
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
              {source === 'git' && (
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-[1fr_160px]">
                  <Field label="Repository" htmlFor="repo" required hint="owner/name or full clone URL">
                    <TextInput
                      id="repo"
                      value={repo}
                      onChange={(e) => setRepo(e.target.value)}
                      placeholder="acme/api-gateway"
                    />
                  </Field>
                  <Field label="Branch" htmlFor="branch">
                    <TextInput id="branch" value={branch} onChange={(e) => setBranch(e.target.value)} />
                  </Field>
                </div>
              )}
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
              {source === 'dockerfile' && (
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
                  <Field label="Repository" htmlFor="repo2" required>
                    <TextInput
                      id="repo2"
                      value={repo}
                      onChange={(e) => setRepo(e.target.value)}
                      placeholder="acme/web"
                    />
                  </Field>
                  <Field label="Dockerfile path" htmlFor="df">
                    <TextInput id="df" value={dockerfilePath} onChange={(e) => setDockerfilePath(e.target.value)} />
                  </Field>
                </div>
              )}
              {source === 'compose' && (
                <Field label="Compose file path" htmlFor="compose" hint="Relative to the repository root.">
                  <TextInput id="compose" value={composePath} onChange={(e) => setComposePath(e.target.value)} />
                </Field>
              )}
            </div>
          </Panel>

          {/* Identity + networking */}
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
                  placeholder="3000"
                />
              </Field>
            </div>
          </Panel>

          {/* Resources — per replica, ganged with replica count */}
          <Panel title="Resources" description="Limits are per replica. Total request is limit × replica count.">
            <div className="flex flex-col gap-5">
              <Field label={`Replicas — ${replicas}`} htmlFor="replicas">
                <Slider id="replicas" value={replicas} onChange={setReplicas} min={1} max={8} step={1} />
              </Field>
              <div className="grid grid-cols-1 gap-5 sm:grid-cols-2">
                <Field label={`CPU per replica — ${cpu.toFixed(2)} cores`} htmlFor="cpu">
                  <Slider id="cpu" value={cpu} onChange={setCpu} min={0.25} max={4} step={0.25} />
                  <Hint>Total request: {cpuReq.toFixed(2)} cores</Hint>
                </Field>
                <Field label={`Memory per replica — ${memory} GiB`} htmlFor="mem">
                  <Slider id="mem" value={memory} onChange={setMemory} min={0.5} max={8} step={0.5} />
                  <Hint>Total request: {memReq} GiB</Hint>
                </Field>
              </div>
            </div>
          </Panel>

          {/* Environment variables */}
          <Panel
            title="Environment"
            description="Injected at container start. Mark sensitive values as secret."
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
        </div>

        {/* Live host-impact sidebar */}
        <aside className="flex flex-col gap-4 lg:sticky lg:top-4 lg:self-start">
          <Panel title="Host impact" description="Where this workload's total request lands on the host.">
            <div className="flex flex-col gap-4">
              <AllocationRail label="CPU" triple={cpuTriple} requested={cpuReq} />
              <AllocationRail label="Memory" triple={hostHealth.memory} requested={memReq} />
            </div>
          </Panel>
          <Button className="w-full" disabled={!canSubmit || submitting} onClick={submit}>
            {submitting ? 'Scheduling…' : 'Deploy application'}
          </Button>
          {!canSubmit && (
            <p className="text-center text-xs text-muted-foreground">Name, source, and port are required.</p>
          )}
        </aside>
      </div>
    </div>
  )
}
