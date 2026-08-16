'use client'

import { createContext, useContext, useEffect, useState } from 'react'
import { usePathname, useRouter } from 'next/navigation'

import { client } from '@/lib/api/client'
import { ApiError } from '@/lib/api/problem'
import type { components } from '@/lib/api/schema'

export type CurrentUser = components['schemas']['CurrentUserDto']
export type SetupStatus = components['schemas']['SetupStatusDto']

interface Session {
  user: CurrentUser | null
  setup: SetupStatus | null
  loading: boolean
  /** The API did not answer at all. Distinct from "answered, and said no". */
  unreachable: boolean
  refresh: () => Promise<void>
  logout: () => Promise<void>
  can: (permission: string) => boolean
}

const SessionContext = createContext<Session | null>(null)

const PUBLIC = new Set(['/setup', '/login'])

/**
 * Whether a failure means the API is not there, rather than that it refused.
 *
 * Three ways to be sure the request never reached Airside:
 *
 * - Not an ApiError at all, so `fetch` itself failed — DNS, refused connection.
 * - A gateway status. Caddy answers 502 when the API container is not up.
 * - Any other 5xx that is not shaped like an Airside error. This is the one that
 *   is easy to miss: Next's dev proxy returns a plain-text `500 Internal Server
 *   Error` for a dead upstream, which is indistinguishable from a real API fault
 *   by status alone. Airside's own 500 always carries a `code` and a `type`
 *   (`internal.unhandled`), because its exception handler puts them there — so a
 *   5xx with neither did not come from Airside.
 */
function isUnreachable(err: unknown): boolean {
  if (!(err instanceof ApiError)) return true
  if (err.status === 502 || err.status === 503 || err.status === 504) return true
  return err.status >= 500 && !err.problem.code && !err.problem.type
}

export function SessionProvider({ children }: { children: React.ReactNode }) {
  const [user, setUser] = useState<CurrentUser | null>(null)
  const [setup, setSetup] = useState<SetupStatus | null>(null)
  const [loading, setLoading] = useState(true)
  const [unreachable, setUnreachable] = useState(false)
  const pathname = usePathname()
  const router = useRouter()

  async function refresh() {
    let status
    try {
      status = await client.GET('/api/v1/setup/status')
      setUnreachable(false)
    } catch (err) {
      // Recorded rather than swallowed. Without this the shell cannot tell
      // "still starting up" from "there is nothing on the other end", and shows
      // a loading state forever for a condition that will never resolve on its
      // own — which is the wrong answer to give someone whose API is down.
      setUnreachable(isUnreachable(err))
      throw err
    }

    const nextSetup = status.data ?? null
    setSetup(nextSetup)

    if (nextSetup && !nextSetup.setupCompleted) {
      setUser(null)
      return
    }

    try {
      const me = await client.GET('/api/v1/auth/me')
      setUser(me.data ?? null)
    } catch (err) {
      setUser(null)
      if (!(err instanceof ApiError) || err.status !== 401) throw err
    }
  }

  useEffect(() => {
    refresh()
      .catch(() => {
        setUser(null)
      })
      .finally(() => setLoading(false))
  }, [])

  useEffect(() => {
    if (loading) return
    if (setup && !setup.setupCompleted && pathname !== '/setup') {
      router.replace('/setup')
      return
    }
    if (setup?.setupCompleted && !user && (pathname === '/setup' || !PUBLIC.has(pathname))) {
      router.replace('/login')
    }
    if (user && (pathname === '/login' || pathname === '/setup')) {
      router.replace('/dashboard')
    }
  }, [loading, setup, user, pathname, router])

  async function logout() {
    try {
      await client.POST('/api/v1/auth/logout')
    } catch {
      /* still clear locally */
    }
    setUser(null)
    router.replace('/login')
  }

  return (
    <SessionContext.Provider
      value={{
        user,
        setup,
        loading,
        unreachable,
        refresh,
        logout,
        can: (permission) => user?.permissions?.includes(permission) ?? false,
      }}
    >
      {children}
    </SessionContext.Provider>
  )
}

export function useSession() {
  const ctx = useContext(SessionContext)
  if (!ctx) throw new Error('useSession must be used inside SessionProvider')
  return ctx
}
