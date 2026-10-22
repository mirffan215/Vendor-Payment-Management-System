import { useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ApiError, auditEvidenceUrl, getAuditEvents, verifyAuditEvidence, type AuditEventDto, type AuditFilters, type EvidenceVerification } from '../api/client'
import { useAction } from '../components/Busy'
import { ProblemAlert } from '../components/ProblemText'
import { formatDateTime, formatNumber, type CalendarPreference } from '../i18n/format'

type Status = 'loading' | 'ready' | 'forbidden' | 'error'

export function AuditPage({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const [events, setEvents] = useState<AuditEventDto[]>([])
  const [next, setNext] = useState<number | null>(null)
  const [status, setStatus] = useState<Status>('loading')
  const [busy, setBusy] = useState(false)
  const [draft, setDraft] = useState<AuditFilters>({})
  const [filters, setFilters] = useState<AuditFilters>({})
  const [verified, setVerified] = useState<EvidenceVerification>()
  const act = useAction()

  const load = useCallback(async (after?: number | null) => {
    // The first page is covered by the "loading" status; only follow-up pages need a busy flag.
    if (after) setBusy(true)
    try {
      const page = await getAuditEvents(after, filters)
      setEvents((prev) => (after ? [...prev, ...page.items] : page.items))
      setNext(page.nextAfterSequence ?? null)
      setStatus('ready')
    } catch (error) {
      setStatus(error instanceof ApiError && error.status === 403 ? 'forbidden' : 'error')
    } finally {
      setBusy(false)
    }
  }, [filters])

  useEffect(() => {
    void load()
  }, [load])

  return (
    <section aria-labelledby="audit-title" className="card">
      <h1 id="audit-title">{t('audit.title')}</h1>
      <p>{t('audit.intro')}</p>
      <form className="row" onSubmit={(e) => { e.preventDefault(); setStatus('loading'); setFilters(draft) }}>
        <label>{t('audit.filter.action')}<input dir="ltr" value={draft.action ?? ''} onChange={(e) => setDraft({ ...draft, action: e.target.value })} /></label>
        <label>{t('audit.filter.entityType')}<input dir="ltr" value={draft.entityType ?? ''} onChange={(e) => setDraft({ ...draft, entityType: e.target.value })} /></label>
        <label>{t('audit.filter.from')}<input type="date" value={draft.from ?? ''} onChange={(e) => setDraft({ ...draft, from: e.target.value })} /></label>
        <label>{t('audit.filter.to')}<input type="date" value={draft.to ?? ''} onChange={(e) => setDraft({ ...draft, to: e.target.value })} /></label>
        <button type="submit">{t('audit.filter.apply')}</button>
      </form>
      <div className="row">
        <a href={auditEvidenceUrl(filters)} download>{t('audit.evidence.download')}</a>
        <label>
          {t('audit.evidence.verify')}
          <input type="file" accept=".zip,application/zip" onChange={(e) => { const f = e.target.files?.[0]; if (f) void act.run(() => verifyAuditEvidence(f)).then(setVerified) }} />
        </label>
      </div>
      <p className="muted">{t('audit.evidence.note')}</p>
      <ProblemAlert error={act.error} />
      {verified && (
        <div role="status" className={verified.isValid ? undefined : 'alert'}>
          <strong>{verified.isValid ? t('audit.evidence.valid', { count: verified.eventsChecked }) : t('audit.evidence.invalid')}</strong>
          <ul className="plain">
            <li>{t('audit.evidence.signature')}: {t(verified.signatureValid ? 'reports.yes' : 'reports.no')}</li>
            <li>{t('audit.evidence.files')}: {t(verified.filesIntact ? 'reports.yes' : 'reports.no')}</li>
            <li>{t('audit.evidence.events')}: {t(verified.eventsIntact ? 'reports.yes' : 'reports.no')}</li>
            <li>{t('audit.evidence.links')}: {t(verified.chainLinked ? 'reports.yes' : 'reports.no')}</li>
          </ul>
        </div>
      )}
      {status === 'loading' && <p role="status">{t('audit.loading')}</p>}
      {status === 'forbidden' && <p role="alert">{t('audit.forbidden')}</p>}
      {status === 'error' && <p role="alert">{t('audit.error')}</p>}
      {status === 'ready' && events.length === 0 && <p>{t('audit.empty')}</p>}
      {status === 'ready' && events.length > 0 && (
        <div className="table-wrap" tabIndex={0}>
          <table>
            <thead>
              <tr>
                <th scope="col">{t('audit.columns.sequence')}</th>
                <th scope="col">{t('audit.columns.time')}</th>
                <th scope="col">{t('audit.columns.action')}</th>
                <th scope="col">{t('audit.columns.entity')}</th>
                <th scope="col">{t('audit.columns.actor')}</th>
              </tr>
            </thead>
            <tbody>
              {events.map((e) => (
                <tr key={e.id}>
                  <td>{formatNumber(e.sequence, i18n.language)}</td>
                  <td>{formatDateTime(e.occurredAt, i18n.language, calendar)}</td>
                  <td dir="ltr">{e.action}</td>
                  <td dir="ltr">
                    {e.entityType}
                    {e.entityId ? ` ${e.entityId}` : ''}
                  </td>
                  <td>{t(`audit.actor.${e.actorType}`, { defaultValue: e.actorType })}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {status === 'ready' && next !== null && (
        <button type="button" disabled={busy} onClick={() => void load(next)}>
          {busy ? t('audit.loading') : t('audit.loadMore')}
        </button>
      )}
    </section>
  )
}
