'use client'

import { useEffect, useState } from 'react'
import { AlertTriangle, Check, ShieldCheck, ShieldOff } from 'lucide-react'

import { ProblemBanner } from '@/components/problem-banner'
import { Button } from '@/components/ui/button'
import { CopyField } from '@/components/ui/copy-field'
import { Field, TextInput } from '@/components/ui/field'
import { Panel } from '@/components/ui/panel'
import { QrCode } from '@/components/ui/qr-code'
import { client } from '@/lib/api/client'
import type { components } from '@/lib/api/schema'

type Status = components['schemas']['MfaStatusDto']
type Enrolment = components['schemas']['MfaEnrolmentDto']

/**
 * Enrolling, confirming and removing the second factor for the signed-in
 * account.
 *
 * The shape of this follows one constraint: the recovery codes are shown once
 * and are the only way back in if the authenticator is lost. So they are not a
 * footnote under the QR code — confirming enrolment is blocked until they have
 * been explicitly acknowledged, and the acknowledgement is a checkbox with the
 * consequence spelled out rather than a "Done" button someone clicks past.
 */
export function MfaPanel() {
  const [status, setStatus] = useState<Status | null>(null)
  const [enrolment, setEnrolment] = useState<Enrolment | null>(null)
  const [code, setCode] = useState('')
  const [savedCodes, setSavedCodes] = useState(false)
  const [disabling, setDisabling] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<unknown>(null)

  useEffect(() => {
    client
      .GET('/api/v1/account/mfa')
      .then((r) => setStatus(r.data ?? null))
      .catch(setError)
  }, [])

  async function refresh() {
    const r = await client.GET('/api/v1/account/mfa')
    setStatus(r.data ?? null)
  }

  async function run(action: () => Promise<void>) {
    setBusy(true)
    setError(null)
    try {
      await action()
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  const start = () =>
    run(async () => {
      const r = await client.POST('/api/v1/account/mfa/enrol', {})
      setEnrolment(r.data ?? null)
      setSavedCodes(false)
      setCode('')
      await refresh()
    })

  const confirm = () =>
    run(async () => {
      await client.POST('/api/v1/account/mfa/confirm', { body: { code } })
      setEnrolment(null)
      setCode('')
      setSavedCodes(false)
      await refresh()
    })

  const disable = () =>
    run(async () => {
      await client.POST('/api/v1/account/mfa/disable', { body: { code } })
      setCode('')
      setDisabling(false)
      await refresh()
    })

  const active = status?.confirmed === true

  return (
    <Panel
      title="Two-factor authentication"
      description="A code from an authenticator app, in addition to the password."
      actions={
        active ? (
          <span className="flex items-center gap-1.5 font-mono text-xs text-running">
            <ShieldCheck className="size-3.5" /> active
          </span>
        ) : (
          <span className="flex items-center gap-1.5 font-mono text-xs text-muted-foreground">
            <ShieldOff className="size-3.5" /> off
          </span>
        )
      }
    >
      {error != null && (
        <div className="mb-4">
          <ProblemBanner error={error} />
        </div>
      )}

      {enrolment != null ? (
        <Enrolling
          enrolment={enrolment}
          code={code}
          onCode={setCode}
          savedCodes={savedCodes}
          onSavedCodes={setSavedCodes}
          busy={busy}
          onConfirm={() => void confirm()}
          onCancel={() => {
            setEnrolment(null)
            setCode('')
            setError(null)
          }}
        />
      ) : active ? (
        <Active
          status={status}
          disabling={disabling}
          onDisabling={setDisabling}
          code={code}
          onCode={setCode}
          busy={busy}
          onDisable={() => void disable()}
        />
      ) : (
        <div>
          <p className="text-sm text-muted-foreground">
            An Airside login is a root login on this host — whoever holds it can run any container.
            A second factor means a leaked password is not enough on its own.
          </p>
          {status?.enrolled === true && (
            <p className="mt-3 flex items-start gap-2 text-xs text-degraded">
              <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
              An enrolment was started but never confirmed, so it is not protecting anything. Starting
              again replaces it and issues fresh recovery codes.
            </p>
          )}
          <div className="mt-4">
            <Button onClick={() => void start()} disabled={busy}>
              {busy ? 'Starting…' : status?.enrolled === true ? 'Start again' : 'Set up'}
            </Button>
          </div>
        </div>
      )}
    </Panel>
  )
}

function Enrolling({
  enrolment,
  code,
  onCode,
  savedCodes,
  onSavedCodes,
  busy,
  onConfirm,
  onCancel,
}: {
  enrolment: Enrolment
  code: string
  onCode: (v: string) => void
  savedCodes: boolean
  onSavedCodes: (v: boolean) => void
  busy: boolean
  onConfirm: () => void
  onCancel: () => void
}) {
  return (
    <div className="flex flex-col gap-5">
      <div className="flex flex-col gap-4 sm:flex-row sm:items-start">
        <QrCode value={enrolment.provisioningUri} label="Scan this with your authenticator app" />
        <div className="min-w-0 flex-1">
          <p className="text-sm font-medium text-foreground">1. Scan this</p>
          <p className="mt-1 text-xs text-muted-foreground">
            Any TOTP app — 1Password, Bitwarden, Aegis, Google Authenticator. If the camera cannot
            reach the screen, enter this key by hand instead:
          </p>
          <div className="mt-2">
            <CopyField value={enrolment.secret} masked />
          </div>
        </div>
      </div>

      <div>
        <p className="text-sm font-medium text-foreground">2. Save these recovery codes</p>
        <p className="mt-1 text-xs text-muted-foreground">
          Shown once. Only their hashes are stored, so this screen is the one and only time they
          exist in readable form. Each works once, in place of a code.
        </p>
        <ul className="mt-2 grid grid-cols-2 gap-x-4 gap-y-1 rounded-md border border-border bg-muted/40 p-3 font-mono text-xs sm:grid-cols-3">
          {enrolment.recoveryCodes.map((c) => (
            <li key={c} className="text-foreground">
              {c}
            </li>
          ))}
        </ul>
        <div className="mt-2 flex justify-end">
          <CopyField value={enrolment.recoveryCodes.join('\n')} className="w-auto" />
        </div>

        <label className="mt-3 flex items-start gap-2 text-xs text-foreground">
          <input
            type="checkbox"
            checked={savedCodes}
            onChange={(e) => onSavedCodes(e.target.checked)}
            className="mt-0.5 size-3.5 accent-foreground"
          />
          <span>
            I have stored these somewhere other than this machine. Without them, losing the
            authenticator means losing the dashboard, and the way back in is SSH to the host.
          </span>
        </label>
      </div>

      <div>
        <p className="text-sm font-medium text-foreground">3. Confirm a code</p>
        <p className="mt-1 text-xs text-muted-foreground">
          Nothing changes until this succeeds — the second factor is not active while a QR code sits
          unscanned.
        </p>
        <div className="mt-2 flex items-end gap-2">
          <Field label="Code from the app" htmlFor="mfa-confirm" className="w-40">
            <TextInput
              id="mfa-confirm"
              inputMode="numeric"
              autoComplete="one-time-code"
              mono
              maxLength={6}
              value={code}
              onChange={(e) => onCode(e.target.value.replace(/\D/g, ''))}
              className="tracking-[0.3em]"
            />
          </Field>
          <Button onClick={onConfirm} disabled={busy || code.length !== 6 || !savedCodes}>
            {busy ? 'Confirming…' : 'Turn on'}
          </Button>
          <Button variant="ghost" onClick={onCancel} disabled={busy}>
            Cancel
          </Button>
        </div>
        {!savedCodes && (
          <p className="mt-2 text-xs text-muted-foreground">
            Acknowledge the recovery codes above first.
          </p>
        )}
      </div>
    </div>
  )
}

function Active({
  status,
  disabling,
  onDisabling,
  code,
  onCode,
  busy,
  onDisable,
}: {
  status: Status | null
  disabling: boolean
  onDisabling: (v: boolean) => void
  code: string
  onCode: (v: string) => void
  busy: boolean
  onDisable: () => void
}) {
  const remaining = Number(status?.recoveryCodesRemaining ?? 0)

  return (
    <div>
      <p className="flex items-center gap-2 text-sm text-foreground">
        <Check className="size-4 text-running" />
        Sign-in on this account asks for a code.
      </p>

      <p className="mt-2 text-xs text-muted-foreground">
        {remaining} recovery {remaining === 1 ? 'code' : 'codes'} left.
      </p>
      {remaining <= 2 && (
        <p className="mt-1 flex items-start gap-2 text-xs text-degraded">
          <AlertTriangle className="mt-0.5 size-3.5 shrink-0" />
          {remaining === 0
            ? 'None left. Losing the authenticator now means recovering through the host.'
            : 'Almost out. Turning the second factor off and setting it up again issues a fresh set.'}
        </p>
      )}

      {disabling ? (
        <div className="mt-4 rounded-md border border-border p-3">
          <p className="text-sm text-foreground">Turn off two-factor authentication</p>
          <p className="mt-1 text-xs text-muted-foreground">
            A current code is required — a stolen session must not be enough to remove the factor
            that the session alone was not supposed to defeat. A recovery code works too.
          </p>
          <div className="mt-3 flex items-end gap-2">
            <Field label="Code" htmlFor="mfa-disable" className="w-48">
              <TextInput
                id="mfa-disable"
                autoComplete="one-time-code"
                mono
                value={code}
                onChange={(e) => onCode(e.target.value.trim())}
              />
            </Field>
            <Button variant="destructive" onClick={onDisable} disabled={busy || code.length < 6}>
              {busy ? 'Turning off…' : 'Turn off'}
            </Button>
            <Button variant="ghost" onClick={() => onDisabling(false)} disabled={busy}>
              Keep it on
            </Button>
          </div>
        </div>
      ) : (
        <div className="mt-4">
          <Button variant="outline" onClick={() => onDisabling(true)}>
            Turn off
          </Button>
        </div>
      )}
    </div>
  )
}
