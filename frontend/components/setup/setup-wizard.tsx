'use client'

import { useState } from 'react'
import { useRouter } from 'next/navigation'
import { ArrowRight, Check } from 'lucide-react'

import { BrandMark } from '@/components/brand/mark'
import { ProblemBanner } from '@/components/problem-banner'
import { Button } from '@/components/ui/button'
import { Field, TextInput } from '@/components/ui/field'
import { client } from '@/lib/api/client'
import { useSession } from '@/lib/session'
import { cn } from '@/lib/utils'

const STEPS = ['Setup token', 'Admin account', 'Instance'] as const

export function SetupWizard() {
  const router = useRouter()
  const { refresh } = useSession()
  const [step, setStep] = useState(0)
  const [token, setToken] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [instanceName, setInstanceName] = useState('')
  const [error, setError] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)

  /**
   * Whether the account exists, even though we are still on this screen.
   *
   * The setup token is consumed by the first successful call, so once this is
   * true the wizard can never be run again — and re-posting it answers
   * "the setup token is missing, expired, or incorrect", which is true and
   * completely misleading about what happened.
   */
  const [accountCreated, setAccountCreated] = useState(false)

  const canContinue =
    (step === 0 && token.length >= 16) ||
    (step === 1 && email.includes('@') && password.length >= 8 && displayName.length > 0) ||
    (step === 2 && instanceName.length > 0)

  async function finish() {
    setBusy(true)
    setError(null)
    try {
      // Guarded, not just attempted once. These are two calls and the first is
      // not repeatable, so a failure in the second must not drag the first back
      // through a token that no longer exists.
      if (!accountCreated) {
        await client.POST('/api/v1/setup/complete', {
          body: {
            setupToken: token,
            email,
            password,
            displayName,
            instanceName,
          },
        })

        setAccountCreated(true)
      }

      await client.POST('/api/v1/auth/login', {
        body: { email, password, totpCode: null },
      })
      await refresh()
      router.replace('/dashboard')
    } catch (err) {
      setError(err)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex min-h-dvh flex-col items-center justify-center bg-background px-4 py-10">
      <BrandMark size="lg" />
      <div className="mt-6 w-full max-w-lg rounded-xl border border-border bg-card shadow-2xl">
        <ol className="flex items-center gap-1 border-b border-border px-4 py-3">
          {STEPS.map((label, i) => (
            <li key={label} className="flex flex-1 items-center gap-2">
              <span
                className={cn(
                  'grid size-6 shrink-0 place-items-center rounded-full border font-mono text-xs',
                  i < step && 'border-running/50 bg-running-soft text-running',
                  i === step && 'border-primary bg-primary/15 text-primary',
                  i > step && 'border-border text-muted-foreground',
                )}
              >
                {i < step ? <Check className="size-3.5" /> : i + 1}
              </span>
              <span className={cn('hidden truncate text-xs sm:block', i === step ? 'text-foreground' : 'text-muted-foreground')}>
                {label}
              </span>
            </li>
          ))}
        </ol>

        <div className="flex flex-col gap-4 px-6 py-6">
          {step === 0 && (
            <>
              <h1 className="font-display text-xl font-semibold">One-time setup token</h1>
              <p className="text-sm text-muted-foreground">
                Printed on the API console on first run. It is consumed here and never stored.
              </p>
              <Field label="Setup token" htmlFor="token" required>
                <TextInput id="token" mono value={token} onChange={(e) => setToken(e.target.value)} autoComplete="off" />
              </Field>
            </>
          )}
          {step === 1 && (
            <>
              <h1 className="font-display text-xl font-semibold">Create the admin account</h1>
              <p className="text-sm text-muted-foreground">There is no default account. This is the first Super Admin.</p>
              <Field label="Display name" htmlFor="name" required>
                <TextInput id="name" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
              </Field>
              <Field label="Email" htmlFor="email" required>
                <TextInput id="email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
              </Field>
              <Field label="Password" htmlFor="password" required hint="At least 8 characters.">
                <TextInput id="password" type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
              </Field>
            </>
          )}
          {step === 2 && (
            <>
              <h1 className="font-display text-xl font-semibold">Name this instance</h1>
              <p className="text-sm text-muted-foreground">Shown in the header. One server, one name.</p>
              <Field label="Instance name" htmlFor="instance" required>
                <TextInput id="instance" value={instanceName} onChange={(e) => setInstanceName(e.target.value)} />
              </Field>
            </>
          )}
          {/*
            Said before the error, because it changes what the error means. Once
            the account exists the only way forward is signing in — the wizard
            cannot be repeated, and an operator staring at a failure on the last
            step has no way to know their account was created a moment ago.
          */}
          {accountCreated && (
            <div className="rounded-lg border border-running/40 bg-running-soft/40 px-4 py-3">
              <p className="text-sm text-foreground">
                Your administrator account was created. Setup is finished and the
                one-time token has been used, so continue by signing in.
              </p>
              <button
                type="button"
                onClick={() => router.replace('/login')}
                className="mt-2 text-xs text-muted-foreground underline underline-offset-4 hover:text-foreground"
              >
                Go to sign in
              </button>
            </div>
          )}

          {error != null && <ProblemBanner error={error} />}
        </div>

        <div className="flex items-center justify-between border-t border-border px-6 py-4">
          <Button variant="ghost" onClick={() => setStep((s) => Math.max(0, s - 1))} disabled={step === 0 || busy}>
            Back
          </Button>
          {step < STEPS.length - 1 ? (
            <Button onClick={() => setStep((s) => s + 1)} disabled={!canContinue}>
              Continue <ArrowRight />
            </Button>
          ) : (
            <Button onClick={finish} disabled={!canContinue || busy}>
              {busy ? 'Finishing…' : 'Finish setup'}
            </Button>
          )}
        </div>
      </div>
      <p className="mt-6 font-mono text-xs text-muted-foreground">First run · the token works once</p>
    </div>
  )
}
