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
  refresh: () => Promise<void>
  logout: () => Promise<void>
  can: (permission: string) => boolean
}

const SessionContext = createContext<Session | null>(null)

const PUBLIC = new Set(['/setup', '/login'])

export function SessionProvider({ children }: { children: React.ReactNode }) {
  const [user, setUser] = useState<CurrentUser | null>(null)
  const [setup, setSetup] = useState<SetupStatus | null>(null)
  const [loading, setLoading] = useState(true)
  const pathname = usePathname()
  const router = useRouter()

  async function refresh() {
    const status = await client.GET('/api/v1/setup/status')
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
