import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { BrowserRouter, Link, Navigate, Route, Routes } from 'react-router-dom'
import { SessionProvider } from './auth/SessionContext'
import { useSession } from './auth/session'
import { Preferences } from './components/Preferences'
import type { CalendarPreference } from './i18n/format'
import { AuditPage } from './pages/AuditPage'
import { HomePage } from './pages/HomePage'
import { LoginPage } from './pages/LoginPage'

function Shell() {
  const { t } = useTranslation()
  const { state, signOut } = useSession()
  const [calendar, setCalendar] = useState<CalendarPreference>('gregorian')

  if (state.status === 'loading') return <p role="status">…</p>

  return (
    <>
      <a className="skip" href="#main">
        {t('nav.skipToContent')}
      </a>
      <header className="top">
        <strong>{t('app.name')}</strong>
        {state.status === 'authenticated' && (
          <nav aria-label={t('nav.home')}>
            <Link to="/">{t('nav.home')}</Link>
            <Link to="/audit">{t('nav.audit')}</Link>
          </nav>
        )}
        <Preferences calendar={calendar} onCalendarChange={setCalendar} />
        {state.status === 'authenticated' && (
          <button type="button" onClick={() => void signOut()}>
            {t('nav.signOut')}
          </button>
        )}
      </header>
      <main id="main">
        <Routes>
          <Route path="/login" element={<LoginPage />} />
          {state.status === 'authenticated' ? (
            <>
              <Route path="/" element={<HomePage />} />
              <Route path="/audit" element={<AuditPage calendar={calendar} />} />
              <Route path="*" element={<Navigate to="/" replace />} />
            </>
          ) : (
            <Route path="*" element={<Navigate to="/login" replace />} />
          )}
        </Routes>
      </main>
    </>
  )
}

export default function App() {
  return (
    <BrowserRouter>
      <SessionProvider>
        <Shell />
      </SessionProvider>
    </BrowserRouter>
  )
}
