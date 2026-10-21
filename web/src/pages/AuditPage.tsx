import { useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ApiError, getAuditEvents, type AuditEventDto } from '../api/client'
import { formatDateTime, formatNumber, type CalendarPreference } from '../i18n/format'

type Status = 'loading' | 'ready' | 'forbidden' | 'error'

export function AuditPage({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const [events, setEvents] = useState<AuditEventDto[]>([])
  const [next, setNext] = useState<number | null>(null)
  const [status, setStatus] = useState<Status>('loading')
  const [busy, setBusy] = useState(false)

  const load = useCallback(async (after?: number | null) => {
    // The first page is covered by the "loading" status; only follow-up pages need a busy flag.
    if (after) setBusy(true)
    try {
      const page = await getAuditEvents(after)
      setEvents((prev) => (after ? [...prev, ...page.items] : page.items))
      setNext(page.nextAfterSequence ?? null)
      setStatus('ready')
    } catch (error) {
      setStatus(error instanceof ApiError && error.status === 403 ? 'forbidden' : 'error')
    } finally {
      setBusy(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  return (
    <section aria-labelledby="audit-title" className="card">
      <h1 id="audit-title">{t('audit.title')}</h1>
      <p>{t('audit.intro')}</p>
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
