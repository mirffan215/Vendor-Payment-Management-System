import { useCallback, useState } from 'react'

/** Runs an async action with a busy flag and captures its error for display. */
export function useAction() {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<unknown>()
  const run = useCallback(async <T,>(fn: () => Promise<T>): Promise<T | undefined> => {
    setBusy(true)
    setError(undefined)
    try {
      return await fn()
    } catch (e) {
      setError(e)
      return undefined
    } finally {
      setBusy(false)
    }
  }, [])
  return { busy, error, run, clear: () => setError(undefined) }
}
