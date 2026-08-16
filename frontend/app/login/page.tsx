'use client'

import { useState } from 'react'
import { useRouter } from 'next/navigation'
import { Eye, EyeOff } from 'lucide-react'

import { BrandMark } from '@/components/brand/mark'
import { ProblemBanner } from '@/components/problem-banner'
import { Button } from '@/components/ui/button'
import { TextInput } from '@/components/ui/field'
import { client } from '@/lib/api/client'
import { ApiError } from '@/lib/api/problem'
import { useSession } from '@/lib/session'
import { cn } from '@/lib/utils'

export default function LoginPage() {
  const router = useRouter()
  const { refresh, setup } = useSession()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [totp, setTotp] = useState('')
  const [showMfa, setShowMfa] = useState(false)
  const [showPassword, setShowPassword] = useState(false)
  const [error, setError] = useState<unknown>(null)
  const [busy, setBusy] = useState(false)

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await client.POST('/api/v1/auth/login', {
        body: { email, password, totpCode: totp || null },
      })
      await refresh()
      router.replace('/dashboard')
    } catch (err) {
      setError(err)

      // The server distinguishes "this account needs a code" from "that code was
      // wrong", and both mean the field belongs on screen. Matching on the code
      // rather than the wording of the message, which is free to change.
      if (
        err instanceof ApiError &&
        (err.code === 'auth.mfa_required' || err.code === 'auth.mfa_invalid')
      ) {
        setShowMfa(true)
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="grid min-h-dvh bg-background lg:grid-cols-[minmax(0,22rem)_1fr]">
      <aside className="flex flex-col justify-between border-b border-border px-6 py-8 lg:border-b-0 lg:border-r lg:px-8 lg:py-10">
        <div>
          <BrandMark size="lg" />
          <p className="mt-8 font-display text-3xl font-semibold tracking-tight text-foreground">
            Unlock this host
          </p>
          <p className="mt-2 max-w-xs text-sm text-muted-foreground">
            One server. Your session stays on this machine — nothing in the URL.
          </p>
        </div>

        <dl className="mt-10 grid grid-cols-2 gap-x-4 gap-y-3 font-mono text-xs lg:mt-0">
          <Fact label="instance" value={setup?.version ? `airside ${setup.version}` : 'airside'} />
          <Fact label="store" value={setup?.storeProvider ?? '—'} />
          <Fact label="setup" value={setup?.setupCompleted ? 'complete' : 'pending'} tone={setup?.setupCompleted ? 'good' : 'warn'} />
          <Fact
            label="domain"
            value={setup?.awaitingDomain ? 'not bound' : 'bound'}
            tone={setup?.awaitingDomain ? 'warn' : 'good'}
          />
        </dl>
      </aside>

      <main className="flex items-center justify-center px-4 py-10 sm:px-8">
        <form onSubmit={onSubmit} className="w-full max-w-md">
          <h1 className="font-display text-xl font-semibold text-foreground">Sign in</h1>
          <p className="mt-1 text-sm text-muted-foreground">Email and password for an account on this host.</p>

          <div className="mt-8 flex flex-col gap-5">
            <LoginField label="Email" htmlFor="email">
              <TextInput
                id="email"
                type="email"
                autoFocus
                autoComplete="username"
                mono
                required
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                className="h-11"
              />
            </LoginField>

            <LoginField label="Password" htmlFor="password">
              <div className="relative">
                <TextInput
                  id="password"
                  type={showPassword ? 'text' : 'password'}
                  autoComplete="current-password"
                  required
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  className="h-11 pr-11"
                />
                <button
                  type="button"
                  onClick={() => setShowPassword((v) => !v)}
                  className="absolute right-2 top-1/2 -translate-y-1/2 rounded p-1.5 text-muted-foreground hover:text-foreground"
                  aria-label={showPassword ? 'Hide password' : 'Show password'}
                >
                  {showPassword ? <EyeOff className="size-4" /> : <Eye className="size-4" />}
                </button>
              </div>
            </LoginField>

            {showMfa ? (
              <LoginField
                label="Authenticator code"
                htmlFor="totp"
                hint="6 digits from the app you enrolled, or one of your recovery codes."
              >
                <TextInput
                  id="totp"
                  autoComplete="one-time-code"
                  autoFocus
                  mono
                  // Long enough for a recovery code, which is the field's other
                  // job and the one that matters when the phone is gone.
                  maxLength={11}
                  value={totp}
                  onChange={(e) => setTotp(e.target.value.replace(/\s/g, ''))}
                  className="h-11 tracking-[0.2em]"
                />
              </LoginField>
            ) : (
              <button
                type="button"
                onClick={() => setShowMfa(true)}
                className="self-start text-xs text-muted-foreground underline-offset-4 hover:text-foreground hover:underline"
              >
                Use an authenticator code
              </button>
            )}

            {error != null && <ProblemBanner error={error} />}

            <Button type="submit" disabled={busy || !email || !password} className="h-11">
              {busy ? 'Signing in…' : 'Unlock'}
            </Button>
          </div>
        </form>
      </main>
    </div>
  )
}

function LoginField({
  label,
  htmlFor,
  hint,
  children,
}: {
  label: string
  htmlFor: string
  hint?: string
  children: React.ReactNode
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <label htmlFor={htmlFor} className="text-sm text-foreground">
        {label}
      </label>
      {children}
      {hint && <p className="text-xs text-muted-foreground">{hint}</p>}
    </div>
  )
}

function Fact({
  label,
  value,
  tone,
}: {
  label: string
  value: string
  tone?: 'good' | 'warn'
}) {
  return (
    <div>
      <dt className="text-[10px] uppercase tracking-wider text-muted-foreground">{label}</dt>
      <dd
        className={cn(
          'mt-0.5 text-foreground',
          tone === 'good' && 'text-running',
          tone === 'warn' && 'text-degraded',
        )}
      >
        {value}
      </dd>
    </div>
  )
}
