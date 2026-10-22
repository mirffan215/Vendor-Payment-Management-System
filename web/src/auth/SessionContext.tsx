import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { ApiError, getSession, setCsrfToken, signOut as apiSignOut, signOutEverywhere as apiSignOutEverywhere } from '../api/client'
import { SessionContext, type SessionState } from './session'

export function SessionProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<SessionState>({ status: 'loading' })

  useEffect(() => {
    let cancelled = false
    getSession()
      .then((session) => {
        if (cancelled) return
        setCsrfToken(session.csrfToken)
        setState({ status: 'authenticated', session })
      })
      .catch((error: unknown) => {
        if (cancelled) return
        setState(error instanceof ApiError && error.status === 401 ? { status: 'anonymous' } : { status: 'error' })
      })
    return () => {
      cancelled = true
    }
  }, [])

  const signOut = useCallback(async () => {
    await apiSignOut()
    setCsrfToken(undefined)
    setState({ status: 'anonymous' })
  }, [])

  const signOutEverywhere = useCallback(async () => {
    await apiSignOutEverywhere()
    setCsrfToken(undefined)
    setState({ status: 'anonymous' })
  }, [])

  const value = useMemo(() => ({ state, signOut, signOutEverywhere }), [state, signOut, signOutEverywhere])
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
}
