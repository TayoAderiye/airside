'use client'

import { useEffect, useRef, useState } from 'react'

import { Panel } from '@/components/ui/panel'
import { client } from '@/lib/api/client'

/**
 * The image build's own output, while it is still running.
 *
 * A deployment's slow part is the build — minutes on a first `npm ci` — and the
 * job step behind it said "Building image" and nothing more for the whole of it,
 * which looks identical to a build that has hung. The API accumulated the output
 * and wrote it only once the build ended, so there was nothing to show even if a
 * screen had asked.
 *
 * Polled rather than streamed. The log is a single capped column on the
 * deployment row, not an append-only feed, so there is no cursor to resume from
 * and a socket would carry the same full string every time anyway.
 */
export function BuildLog({ applicationId, active }: { applicationId: string; active: boolean }) {
  const [content, setContent] = useState<string | null>(null)
  const box = useRef<HTMLPreElement>(null)

  useEffect(() => {
    let cancelled = false
    let timer: ReturnType<typeof setTimeout> | undefined

    async function poll() {
      try {
        // The newest deployment is this one: the screen is reached by starting
        // it, and deployments are returned newest first.
        const list = await client.GET('/api/v1/applications/{id}/deployments', {
          params: { path: { id: applicationId } },
        })

        const deploymentId = list.data?.items?.[0]?.id

        if (deploymentId) {
          const res = await fetch(`/api/v1/deployments/${deploymentId}/log`, {
            credentials: 'include',
          })

          // Plain text, not JSON — the endpoint returns the log as content, so
          // the generated client is the wrong tool for it.
          if (res.ok && !cancelled) setContent(await res.text())
        }
      } catch {
        // A poll that fails is not worth a banner: the next one is two seconds
        // away, and the job's own steps already report real failures.
      }

      if (!cancelled && active) timer = setTimeout(poll, 2000)
    }

    void poll()

    return () => {
      cancelled = true
      if (timer) clearTimeout(timer)
    }
  }, [applicationId, active])

  useEffect(() => {
    if (box.current) box.current.scrollTop = box.current.scrollHeight
  }, [content])

  if (content === null) {
    return null
  }

  return (
    <Panel
      title="Build output"
      description={active ? 'Updating while the image builds.' : 'Final output from this build.'}
      bodyClassName="p-0"
    >
      {content.trim().length === 0 ? (
        <p className="px-4 py-3 text-xs text-muted-foreground">
          Nothing yet. A prebuilt image has no build output — the deployment goes straight to
          creating the container.
        </p>
      ) : (
        <pre
          ref={box}
          className="max-h-96 overflow-auto whitespace-pre-wrap break-all px-4 py-3 font-mono text-xs text-muted-foreground"
        >
          {content}
        </pre>
      )}
    </Panel>
  )
}
