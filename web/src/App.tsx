import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { BrowserRouter, Link, Navigate, Route, Routes } from 'react-router-dom'
import { savePreferences } from './api/client'
import { SessionProvider } from './auth/SessionContext'
import { useSession } from './auth/session'
import { NotificationBell } from './components/NotificationBell'
import { Preferences } from './components/Preferences'
import { setLocale, type Locale } from './i18n'
import type { CalendarPreference } from './i18n/format'
import { AuditPage } from './pages/AuditPage'
import { HomePage } from './pages/HomePage'
import { LoginPage } from './pages/LoginPage'
import { PortalShell } from './pages/portal/PortalShell'
import { ApprovalTaskPage } from './pages/staff/ApprovalTaskPage'
import { ApprovalsInboxPage } from './pages/staff/ApprovalsInboxPage'
import { DelegationsPage } from './pages/staff/DelegationsPage'
import { WorkflowAdminPage } from './pages/staff/WorkflowAdminPage'
import { ErpAdminPage } from './pages/staff/ErpAdminPage'
import { ErpReconciliationPage } from './pages/staff/ErpReconciliationPage'
import { ErpSyncPage } from './pages/staff/ErpSyncPage'
import { NotificationAdminPage } from './pages/staff/NotificationAdminPage'
import { RetentionPage } from './pages/staff/RetentionPage'
import { NotificationsPage } from './pages/staff/NotificationsPage'
import { PaymentsPage } from './pages/staff/PaymentsPage'
import { ReportsPage } from './pages/staff/ReportsPage'
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
  const { t, i18n } = useTranslation()
  const { state, signOut, signOutEverywhere } = useSession()
  const session = state.status === 'authenticated' ? state.session : undefined

  // A saved server-side choice wins when the person signs in on a new browser. Older servers send no calendar; then the browser's own choice stands.
  useEffect(() => {
    if (!session?.calendar) return
    if ((session.locale === 'en' || session.locale === 'ar') && session.locale !== i18n.language) void setLocale(i18n, session.locale as Locale)
    if (session.calendar === 'gregorian' || session.calendar === 'hijri') onCalendarChange(session.calendar)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [session?.userId])

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
            <Link to="/reports">{t('nav.reports')}</Link>
            <Link to="/erp/sync">{t('nav.erp')}</Link>
            <Link to="/delegations">{t('nav.delegations')}</Link>
            <Link to="/workflow">{t('nav.workflow')}</Link>
            <Link to="/bank-changes">{t('nav.bankChanges')}</Link>
            <Link to="/admin">{t('nav.admin')}</Link>
            <Link to="/admin/notifications">{t('nav.notificationAdmin')}</Link>
            <Link to="/admin/retention">{t('nav.retention')}</Link>
            <Link to="/audit">{t('nav.audit')}</Link>
          </nav>
        )}
        <Preferences
          calendar={calendar}
          onCalendarChange={onCalendarChange}
          onSave={state.status === 'authenticated' ? (locale, cal) => void savePreferences(locale, cal).catch(() => undefined) : undefined}
        />
        {state.status === 'authenticated' && <NotificationBell />}
        {state.status === 'authenticated' && (
          <>
            <button type="button" onClick={() => void signOut()}>
              {t('nav.signOut')}
            </button>
            <button type="button" className="link" onClick={() => void signOutEverywhere()}>
              {t('nav.signOutEverywhere')}
            </button>
          </>
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
              <Route path="/erp" element={<ErpAdminPage calendar={calendar} />} />
              <Route path="/erp/sync" element={<ErpSyncPage calendar={calendar} />} />
              <Route path="/erp/reconciliation" element={<ErpReconciliationPage calendar={calendar} />} />
              <Route path="/reports" element={<ReportsPage calendar={calendar} />} />
              <Route path="/notifications" element={<NotificationsPage calendar={calendar} />} />
              <Route path="/admin/notifications" element={<NotificationAdminPage calendar={calendar} />} />
              <Route path="/admin/retention" element={<RetentionPage calendar={calendar} />} />
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
