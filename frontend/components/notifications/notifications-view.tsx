'use client'

import { useEffect, useState } from 'react'

import { ProblemBanner } from '@/components/problem-banner'
import { Button } from '@/components/ui/button'
import { Field, TextInput } from '@/components/ui/field'
import { PageHeader, Panel } from '@/components/ui/panel'
import { client } from '@/lib/api/client'
import type { components } from '@/lib/api/schema'

type Note = components['schemas']['NotificationDto']
type ChannelList = components['schemas']['ChannelListDto']
type Preview = components['schemas']['RoutePreviewDto']

export function NotificationsView() {
  const [notes, setNotes] = useState<Note[]>([])
  const [channels, setChannels] = useState<ChannelList | null>(null)
  const [preview, setPreview] = useState<Preview | null>(null)
  const [minSeverity, setMinSeverity] = useState('warning')
  const [error, setError] = useState<unknown>(null)

  async function load() {
    try {
      const [n, c] = await Promise.all([
        client.GET('/api/v1/notifications', { params: { query: { includeResolved: false } } }),
        client.GET('/api/v1/notification-channels'),
      ])
      setNotes(n.data ?? [])
      setChannels(c.data ?? null)
    } catch (err) {
      setError(err)
    }
  }

  useEffect(() => {
    void load()
  }, [])

  async function acknowledge(id: string) {
    await client.POST('/api/v1/notifications/{id}/acknowledge', { params: { path: { id } } })
    await load()
  }

  async function runPreview() {
    setError(null)
    try {
      const res = await client.POST('/api/v1/notification-channels/preview', {
        body: { routing: null, minimumSeverity: minSeverity, schedule: null },
      })
      setPreview(res.data ?? null)
    } catch (err) {
      setError(err)
    }
  }

  return (
    <div className="flex flex-col gap-5">
      <PageHeader title="Notifications" description="Unresolved events on this host, and the channels that would receive them." />
      {error != null && <ProblemBanner error={error} />}

      <Panel title="Feed" bodyClassName="p-0">
        <ul className="divide-y divide-border">
          {notes.map((n) => (
            <li key={n.id} className="flex items-start justify-between gap-3 px-4 py-3">
              <div>
                <p className="text-sm font-medium">{n.title}</p>
                <p className="text-xs text-muted-foreground">{n.body}</p>
                {n.code && <p className="font-mono text-[11px] text-degraded">{n.code}</p>}
              </div>
              <Button size="sm" variant="outline" onClick={() => void acknowledge(n.id)}>
                Acknowledge
              </Button>
            </li>
          ))}
          {notes.length === 0 && <p className="px-4 py-6 text-sm text-muted-foreground">Nothing unresolved.</p>}
        </ul>
      </Panel>

      <Panel
        title="Channels"
        description="Routing that matches nothing looks like a working channel. Preview against real history."
      >
        {channels?.warning && (
          <p className="mb-3 rounded-md border border-degraded/40 bg-degraded-soft/50 px-3 py-2 text-sm text-degraded">
            {channels.warning}
          </p>
        )}
        <ul className="mb-4 divide-y divide-border rounded-md border border-border">
          {(channels?.channels ?? []).map((c) => (
            <li key={c.id} className="px-3 py-2">
              <p className="text-sm font-medium">{c.name}</p>
              <p className="font-mono text-xs text-muted-foreground">
                {c.kind} · min {c.minimumSeverity} · {c.openNow ? 'open now' : 'asleep'}
              </p>
            </li>
          ))}
        </ul>
        <div className="flex items-end gap-2">
          <Field label="Minimum severity" htmlFor="sev">
            <TextInput id="sev" value={minSeverity} onChange={(e) => setMinSeverity(e.target.value)} />
          </Field>
          <Button variant="outline" onClick={() => void runPreview()}>
            Preview routing
          </Button>
        </div>
        {preview && (
          <div className="mt-3 rounded-md border border-border p-3">
            <p className="text-sm">
              Would send {preview.wouldSend} of {preview.considered}
            </p>
            {preview.warning && <p className="mt-1 text-sm text-degraded">{preview.warning}</p>}
          </div>
        )}
      </Panel>
    </div>
  )
}
