import { useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Navigate, Route, Routes, useNavigate } from 'react-router-dom'
import { ApiError, setCsrfToken } from '../../api/client'
import { getPortalSession, portalLogout, type PortalSessionInfo } from '../../api/portal'
import { Preferences } from '../../components/Preferences'
import type { CalendarPreference } from '../../i18n/format'
import { AcceptPage } from './AcceptPage'
import { PortalHome } from './PortalHome'
import { PortalLoginPage } from './PortalLoginPage'

type State = { status: 'loading' } | { status: 'anonymous' } | { status: 'authenticated'; session: PortalSessionInfo } | { status: 'error' }

/** The vendor portal is a separate area with its own sign-in and session; it never shares state with the staff app. */
export function PortalShell({ calendar, onCalendarChange }: { calendar: CalendarPreference; onCalendarChange: (c: CalendarPreference) => void }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [state, setState] = useState<State>({ status: 'loading' })

  const refresh = useCallback(async () => {
    try {
      setState({ status: 'authenticated', session: await getPortalSession() })
    } catch (e) {
      setState(e instanceof ApiError && e.status === 401 ? { status: 'anonymous' } : { status: 'error' })
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const signOut = async () => {
    await portalLogout()
    setCsrfToken(undefined)
    setState({ status: 'anonymous' })
    navigate('/portal/login')
  }

  return (
    <>
      <a className="skip" href="#main">{t('nav.skipToContent')}</a>
      <header className="top">
        <strong>{t('portal.brand')}</strong>
        <span className="grow" />
        <Preferences calendar={calendar} onCalendarChange={onCalendarChange} />
        {state.status === 'authenticated' && (
          <button type="button" onClick={() => void signOut()}>{t('nav.signOut')}</button>
        )}
      </header>
      <main id="main">
        {state.status === 'loading' && <p role="status">{t('common.loading')}</p>}
        {state.status === 'error' && <p role="alert">{t('errors.generic')}</p>}
        {(state.status === 'anonymous' || state.status === 'authenticated') && (
          <Routes>
            <Route path="accept" element={<AcceptPage onSignedIn={refresh} />} />
            <Route path="login" element={state.status === 'authenticated' ? <Navigate to="/portal" replace /> : <PortalLoginPage onSignedIn={refresh} />} />
            <Route
              path="*"
              element={state.status === 'authenticated' ? <PortalHome session={state.session} calendar={calendar} /> : <Navigate to="/portal/login" replace />}
            />
          </Routes>
        )}
      </main>
    </>
  )
}
