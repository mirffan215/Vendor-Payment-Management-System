import { createContext, useContext } from 'react'
import type { SessionInfo } from '../api/client'

export type SessionState =
  | { status: 'loading' }
  | { status: 'anonymous' }
  | { status: 'authenticated'; session: SessionInfo }
  | { status: 'error' }

export interface SessionContextValue {
  state: SessionState
  signOut: () => Promise<void>
}

export const SessionContext = createContext<SessionContextValue | undefined>(undefined)

export function useSession(): SessionContextValue {
  const ctx = useContext(SessionContext)
  if (!ctx) throw new Error('useSession must be used inside SessionProvider')
  return ctx
}
