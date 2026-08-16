'use client'

import { useEffect, useState } from 'react'

import { BrandMark } from '@/components/brand/mark'

/**
 * Baked in at build time by the Dockerfile, from this package's own version.
 * Undefined in development, which disables the gate — `next dev` runs against
 * whatever API happens to be on the other end and being told off about it every
 * reload would be useless.
 */
const UI_VERSION = process.env.NEXT_PUBLIC_AIRSIDE_UI_VERSION

/**
 * Give the API this long to answer before giving up and rendering anyway.
 * Without it a hung API is worse than a missing one: the dashboard would sit on
 * a blank screen indefinitely rather than loading and showing a real error.
 */
const TIMEOUT_MS = 5000

type Check =
  | { state: 'checking' }
  | { state: 'ok' }
  | { state: 'mismatch'; api: string }

/**
 * The leading <c>major.minor</c> of a version, which is the part that has to
 * agree.
 *
 * Airside is pre-1.0, so a minor bump is the breaking one and patch releases are
 * compatible by definition. Returns null for anything unparseable, which the
 * caller treats as "do not gate" rather than as a mismatch.
 */
function series(version: string | undefined | null): string | null {
  if (!version) return null
  const match = /^(\d+)\.(\d+)(?:[.\-+]|$)/.exec(version.trim())
  return match ? `${match[1]}.${match[2]}` : null
}

/**
 * Refuses to render the dashboard against an API of a different version.
 *
 * The UI and the API ship as separate containers, so they can be updated
 * independently — including by accident, when something replaces one and not the
 * other. What that produces is not a clean failure: the dashboard loads, calls
 * an endpoint whose response has changed shape, and renders a field that is now
 * missing as blank. An operator reads that as "no domains attached" rather than
 * "this screen is lying to me", and acts on it.
 *
 * So the check happens once, before anything renders, and it happens without
 * authentication — a dashboard too old to understand the API is also too old to
 * be sure it can log in.
 */
export function VersionGate({ children }: { children: React.ReactNode }) {
  const [check, setCheck] = useState<Check>(() =>
    series(UI_VERSION) === null ? { state: 'ok' } : { state: 'checking' },
  )
  const [overridden, setOverridden] = useState(false)

  useEffect(() => {
    const ui = series(UI_VERSION)
    if (ui === null) return

    let cancelled = false

    async function compare() {
      try {
        // Deliberately plain fetch rather than the generated client. The client
        // is built from a schema snapshot, and a stale schema is exactly the
        // condition being detected — routing the detection through it would make
        // the check unreliable in the one case it exists for.
        const response = await fetch('/api/v1/version', {
          headers: { accept: 'application/json' },
          cache: 'no-store',
          signal: AbortSignal.timeout(TIMEOUT_MS),
        })

        if (!response.ok) throw new Error(`HTTP ${response.status}`)

        const body: unknown = await response.json()
        const api =
          typeof body === 'object' && body !== null && 'version' in body &&
          typeof (body as { version: unknown }).version === 'string'
            ? (body as { version: string }).version
            : ''

        if (cancelled) return

        const apiSeries = series(api)
        setCheck(
          apiSeries !== null && apiSeries !== ui
            ? { state: 'mismatch', api }
            : { state: 'ok' },
        )
      } catch {
        // Fail open, on purpose. An unreachable or unparseable API is a
        // different problem with its own error handling further in, and blocking
        // here would mean a restarting API looks like a broken dashboard —
        // turning a thirty-second blip into a support question.
        if (!cancelled) setCheck({ state: 'ok' })
      }
    }

    void compare()

    return () => {
      cancelled = true
    }
  }, [])

  if (check.state === 'checking') return null

  if (check.state === 'mismatch' && !overridden) {
    return (
      <VersionMismatch
        ui={UI_VERSION ?? 'unknown'}
        api={check.api}
        onOverride={() => setOverridden(true)}
      />
    )
  }

  return <>{children}</>
}

function VersionMismatch({
  ui,
  api,
  onOverride,
}: {
  ui: string
  api: string
  onOverride: () => void
}) {
  return (
    <div className="grid min-h-dvh place-items-center bg-background px-6">
      <div className="w-full max-w-lg">
        <BrandMark size="lg" />

        <h1 className="mt-8 font-display text-2xl font-semibold tracking-tight text-foreground">
          The dashboard and the API are different versions
        </h1>

        <p className="mt-3 text-sm text-muted-foreground">
          These two ship together and are not meant to differ. Almost always this
          means an update replaced one container and not the other, so the
          dashboard has stopped here rather than showing you screens it may be
          reading wrongly.
        </p>

        <dl className="mt-6 grid grid-cols-[auto_1fr] gap-x-6 gap-y-1 rounded-lg border border-border px-4 py-3 font-mono text-xs">
          <dt className="text-muted-foreground">dashboard</dt>
          <dd className="text-foreground">{ui}</dd>
          <dt className="text-muted-foreground">api</dt>
          <dd className="text-foreground">{api || 'unreadable'}</dd>
        </dl>

        <p className="mt-6 text-sm text-muted-foreground">
          Bring both to the same version on the host:
        </p>

        <pre className="mt-2 overflow-x-auto rounded-lg border border-border bg-card px-4 py-3 font-mono text-xs text-foreground">
          cd /opt/airside{'\n'}
          docker compose up -d
        </pre>

        <p className="mt-6 text-xs text-muted-foreground">
          The <span className="font-mono">airside</span> CLI on the host keeps
          working while this screen is up, and is the better tool for repairing a
          half-finished update.
        </p>

        <button
          type="button"
          onClick={onOverride}
          className="mt-6 text-xs text-muted-foreground underline underline-offset-4 hover:text-foreground"
        >
          Continue anyway — screens may display incorrect values
        </button>
      </div>
    </div>
  )
}
