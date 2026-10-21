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
import { PortalShell } from './pages/portal/PortalShell'
import { AdminPage } from './pages/staff/AdminPage'
import { BankChangesPage } from './pages/staff/BankChangesPage'
import { VendorDetailPage } from './pages/staff/VendorDetailPage'
import { VendorsPage } from './pages/staff/VendorsPage'

interface CalendarProps {
  calendar: CalendarPreference
  onCalendarChange: (calendar: CalendarPreference) => void
}

function StaffShell({ calendar, onCalendarChange }: CalendarProps) {
  const { t } = useTranslation()
  const { state, signOut } = useSession()

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
            <Link to="/vendors">{t('nav.vendors')}</Link>
            <Link to="/bank-changes">{t('nav.bankChanges')}</Link>
            <Link to="/admin">{t('nav.admin')}</Link>
            <Link to="/audit">{t('nav.audit')}</Link>
          </nav>
        )}
        <Preferences calendar={calendar} onCalendarChange={onCalendarChange} />
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
              <Route path="/vendors" element={<VendorsPage calendar={calendar} />} />
              <Route path="/vendors/:id" element={<VendorDetailPage calendar={calendar} />} />
              <Route path="/bank-changes" element={<BankChangesPage calendar={calendar} />} />
              <Route path="/admin" element={<AdminPage calendar={calendar} />} />
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
  const [calendar, setCalendar] = useState<CalendarPreference>('gregorian')
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/portal/*" element={<PortalShell calendar={calendar} onCalendarChange={setCalendar} />} />
        <Route
          path="*"
          element={
            <SessionProvider>
              <StaffShell calendar={calendar} onCalendarChange={setCalendar} />
            </SessionProvider>
          }
        />
      </Routes>
    </BrowserRouter>
  )
}
