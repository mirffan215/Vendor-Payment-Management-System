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
import { ApprovalTaskPage } from './pages/staff/ApprovalTaskPage'
import { ApprovalsInboxPage } from './pages/staff/ApprovalsInboxPage'
import { DelegationsPage } from './pages/staff/DelegationsPage'
import { WorkflowAdminPage } from './pages/staff/WorkflowAdminPage'
import { PaymentsPage } from './pages/staff/PaymentsPage'
import { ProposalDetailPage } from './pages/staff/ProposalDetailPage'
import { AdminPage } from './pages/staff/AdminPage'
import { ExceptionsPage } from './pages/staff/ExceptionsPage'
import { IntakePage } from './pages/staff/IntakePage'
import { InvoiceDetailPage } from './pages/staff/InvoiceDetailPage'
import { InvoicesPage } from './pages/staff/InvoicesPage'
import { VerificationAdminPage } from './pages/staff/VerificationAdminPage'
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
            <Link to="/invoices">{t('nav.invoices')}</Link>
            <Link to="/approvals">{t('nav.approvals')}</Link>
            <Link to="/exceptions">{t('nav.exceptions')}</Link>
            <Link to="/intake">{t('nav.intake')}</Link>
            <Link to="/verification">{t('nav.verification')}</Link>
            <Link to="/payments">{t('nav.payments')}</Link>
            <Link to="/delegations">{t('nav.delegations')}</Link>
            <Link to="/workflow">{t('nav.workflow')}</Link>
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
              <Route path="/invoices" element={<InvoicesPage calendar={calendar} />} />
              <Route path="/invoices/:id" element={<InvoiceDetailPage calendar={calendar} />} />
              <Route path="/approvals" element={<ApprovalsInboxPage calendar={calendar} />} />
              <Route path="/approvals/:taskId" element={<ApprovalTaskPage calendar={calendar} />} />
              <Route path="/payments" element={<PaymentsPage calendar={calendar} />} />
              <Route path="/payments/proposals/:id" element={<ProposalDetailPage calendar={calendar} />} />
              <Route path="/delegations" element={<DelegationsPage calendar={calendar} />} />
              <Route path="/workflow" element={<WorkflowAdminPage />} />
              <Route path="/exceptions" element={<ExceptionsPage calendar={calendar} />} />
              <Route path="/intake" element={<IntakePage calendar={calendar} />} />
              <Route path="/verification" element={<VerificationAdminPage />} />
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
