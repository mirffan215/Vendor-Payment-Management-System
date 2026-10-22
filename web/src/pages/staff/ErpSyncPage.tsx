import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { getSyncDetail, getSyncSummary, listSync, resolveSync, retrySync, type SyncDetail, type SyncRecord } from '../../api/erp'
import { listEntities } from '../../api/staff'
import { useAction } from '../../components/Busy'
import { ErpNav } from '../../components/ErpNav'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDateTime, type CalendarPreference } from '../../i18n/format'

const STATUSES = ['DeadLetter', 'Pending', 'InFlight', 'Succeeded', 'Resolved', 'Superseded'] as const

export function ErpSyncPage({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const [status, setStatus] = useState<string>('DeadLetter')
  const summary = useLoad(getSyncSummary, 'sync-summary')
  const list = useLoad(() => listSync(status || undefined), `sync-${status}`)
  const entities = useLoad(listEntities, 'entities')
  const act = useAction()
  const [open, setOpen] = useState<SyncDetail>()
  const [reason, setReason] = useState('')
  const [reference, setReference] = useState('')

  const entityName = (id: string) => entities.data?.find((e) => e.id === id)?.code ?? id
  const reloadAll = () => {
    summary.reload()
    list.reload()
    setOpen(undefined)
    setReason('')
    setReference('')
  }
  const show = async (r: SyncRecord) => {
    const detail = await act.run(() => getSyncDetail(r.id))
    if (detail) setOpen(detail)
  }

  return (
    <div className="stack erp">
      <ErpNav />
      <section className="card" aria-labelledby="sync-title">
        <h1 id="sync-title">{t('erp.sync.title')}</h1>
        <p>{t('erp.sync.intro')}</p>
        <ProblemAlert error={summary.error ?? list.error ?? act.error} />
        {summary.data && (
          <div className="tiles">
            {(['pending', 'inFlight', 'succeeded', 'deadLetter', 'resolved'] as const).map((k) => (
              <div key={k}><strong>{summary.data![k]}</strong>{t(`erp.sync.summary.${k}`)}</div>
            ))}
          </div>
        )}
        <label>
          {t('erp.sync.filter')}
          <select value={status} onChange={(e) => { setStatus(e.target.value); setOpen(undefined) }}>
            <option value="">{t('erp.sync.all')}</option>
            {STATUSES.map((s) => (<option key={s} value={s}>{t(`erp.sync.status.${s}`)}</option>))}
          </select>
        </label>
        {list.data?.length === 0 && <p>{t('erp.sync.empty')}</p>}
        <ul className="plain">
          {(list.data ?? []).map((r) => (
            <li key={r.id} className="row">
              <strong>{t(`erp.sync.type.${r.type}`)}</strong>
              <span dir="ltr">{entityName(r.legalEntityId)}</span>
              <span>{t(`erp.sync.status.${r.status}`)}</span>
              <span>{t('erp.sync.attempts', { count: r.attempts })}</span>
              {r.lastErrorCode && <span className="alert">{t(`erp.sync.error.${r.lastErrorCode}`, { defaultValue: r.lastErrorCode })}</span>}
              {r.erpReference && <span dir="ltr">{t('erp.sync.reference', { ref: r.erpReference })}</span>}
              {r.status === 'Pending' && <span className="muted">{t('erp.sync.nextAttempt', { date: formatDateTime(r.nextAttemptAt, i18n.language, calendar) })}</span>}
              <button type="button" className="secondary" disabled={act.busy} onClick={() => void show(r)}>{t('erp.sync.details')}</button>
            </li>
          ))}
        </ul>
      </section>

      {open && (
        <section className="card" aria-labelledby="sync-detail-title">
          <h2 id="sync-detail-title">{t('erp.sync.detailTitle')}</h2>
          {open.record.lastError && <p className="muted">{open.record.lastError}</p>}
          {open.record.handlingNote && <p>{open.record.handlingNote}</p>}
          <ul className="plain">
            {open.attempts.map((a, i) => (
              <li key={i} className="row">
                <span>{formatDateTime(a.at, i18n.language, calendar)}</span>
                <span>{a.outcome}</span>
                {a.code && <span dir="ltr">{a.code}</span>}
                {a.message && <span className="muted">{a.message}</span>}
              </li>
            ))}
          </ul>
          {open.record.status === 'DeadLetter' && (
            <div className="stack">
              <label>{t('erp.sync.reason')}<input value={reason} maxLength={500} onChange={(e) => setReason(e.target.value)} /></label>
              <label>{t('erp.sync.erpReference')}<input dir="ltr" value={reference} onChange={(e) => setReference(e.target.value)} /></label>
              <div className="row">
                <button type="button" disabled={act.busy || !reason.trim()} onClick={() => void act.run(() => retrySync(open.record.id, reason)).then((r) => r && reloadAll())}>{t('erp.sync.retry')}</button>
                <button type="button" className="secondary" disabled={act.busy || !reason.trim()} onClick={() => void act.run(() => resolveSync(open.record.id, reason, reference.trim() || null)).then((r) => r && reloadAll())}>
                  {t('erp.sync.resolve')}
                </button>
              </div>
            </div>
          )}
        </section>
      )}
    </div>
  )
}
