import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  RETENTION_CLASSES, approveRetention, cancelRetention, getRetention, getRetentionReport, listHolds, placeHold, proposeRetention, rejectRetention, releaseHold, type RetentionPolicy,
} from '../../api/retention'
import { listEntities } from '../../api/staff'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDate, formatDateTime, formatNumber, type CalendarPreference } from '../../i18n/format'

export function RetentionPage({ calendar }: { calendar: CalendarPreference }) {
  const { t } = useTranslation()
  return (
    <div className="stack erp">
      <section className="card" aria-labelledby="ret-title">
        <h1 id="ret-title">{t('retention.title')}</h1>
        <p>{t('retention.intro')}</p>
        <p className="muted">{t('retention.autoNote')}</p>
      </section>
      <Policy calendar={calendar} />
      <Report calendar={calendar} />
      <Holds calendar={calendar} />
    </div>
  )
}

function Policy({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const state = useLoad(getRetention, 'retention-state')
  const act = useAction()
  const [months, setMonths] = useState<Record<string, string>>({})
  const [note, setNote] = useState('')
  const [comment, setComment] = useState('')
  const active = state.data?.active
  const pending = state.data?.pending
  const monthsOf = (cls: string) => months[cls] ?? String(active?.settings.months[cls] ?? state.data?.defaults[cls] ?? '')

  const propose = async () => {
    const settings = { months: Object.fromEntries(RETENTION_CLASSES.map((c) => [c, Number(monthsOf(c))])), countries: active?.settings.countries ?? {} }
    const r = await act.run(() => proposeRetention(settings, note))
    if (r) { setNote(''); setMonths({}); state.reload() }
  }
  const decide = async (p: RetentionPolicy, action: 'approve' | 'reject') => {
    const r = await act.run(() => (action === 'approve' ? approveRetention(p.id!, comment) : rejectRetention(p.id!, comment)))
    if (r) { setComment(''); state.reload() }
  }

  return (
    <section className="card" aria-labelledby="ret-policy">
      <h2 id="ret-policy">{t('retention.policy')}</h2>
      <ProblemAlert error={state.error ?? act.error} />
      <p role="status">{active ? t('retention.inForce', { version: active.versionNo }) : t('retention.defaultsApply')}</p>
      {pending && (
        <div className="card inner">
          <p role="status">{t('retention.waiting', { version: pending.versionNo })}</p>
          <p>{pending.note}</p>
          <p className="muted">{pending.createdAt ? formatDateTime(pending.createdAt, i18n.language, calendar) : ''}</p>
          <ul className="plain">
            {RETENTION_CLASSES.map((c) => (<li key={c}>{t(`retention.class.${c}`)}: {t('retention.months', { count: pending.settings.months[c] ?? state.data?.defaults[c] })}</li>))}
          </ul>
          <label>{t('erp.comment')}<input value={comment} maxLength={500} onChange={(e) => setComment(e.target.value)} /></label>
          <div className="row">
            <button type="button" disabled={act.busy} onClick={() => void decide(pending, 'approve')}>{t('erp.changes.approve')}</button>
            <button type="button" className="secondary" disabled={act.busy || !comment.trim()} onClick={() => void decide(pending, 'reject')}>{t('erp.changes.reject')}</button>
            <button type="button" className="secondary" disabled={act.busy} onClick={() => void act.run(() => cancelRetention(pending.id!)).then((r) => r && state.reload())}>{t('erp.changes.cancel')}</button>
          </div>
        </div>
      )}
      <form className="stack" onSubmit={(e) => { e.preventDefault(); void propose() }}>
        <div className="table-wrap" tabIndex={0}>
          <table>
            <thead>
              <tr>
                <th scope="col">{t('retention.dataClass')}</th>
                <th scope="col">{t('retention.default')}</th>
                <th scope="col">{t('retention.keepFor')}</th>
                <th scope="col">{t('retention.handling')}</th>
              </tr>
            </thead>
            <tbody>
              {RETENTION_CLASSES.map((c) => (
                <tr key={c}>
                  <th scope="row">{t(`retention.class.${c}`)}</th>
                  <td dir="ltr">{state.data ? formatNumber(state.data.defaults[c], i18n.language) : ''}</td>
                  <td>
                    <input type="number" min={1} max={1200} dir="ltr" aria-label={`${t(`retention.class.${c}`)} — ${t('retention.keepFor')}`} value={monthsOf(c)} onChange={(e) => setMonths({ ...months, [c]: e.target.value })} />
                  </td>
                  <td>{state.data?.disposedAutomatically.includes(c) ? t('retention.handlingAuto') : t('retention.handlingReport')}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <label>{t('erp.note')}<input value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} /></label>
        <button type="submit" disabled={act.busy || !note.trim() || !!pending}>{t('erp.propose')}</button>
      </form>
    </section>
  )
}

function Report({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const report = useLoad(getRetentionReport, 'retention-report')
  return (
    <section className="card" aria-labelledby="ret-report">
      <h2 id="ret-report">{t('retention.report')}</h2>
      <p className="muted">{t('retention.reportNote')}</p>
      <ProblemAlert error={report.error} />
      <div className="table-wrap" tabIndex={0}>
        <table>
          <thead>
            <tr>
              <th scope="col">{t('retention.dataClass')}</th>
              <th scope="col">{t('retention.keepFor')}</th>
              <th scope="col">{t('retention.olderThan')}</th>
              <th scope="col">{t('retention.pastRetention')}</th>
            </tr>
          </thead>
          <tbody>
            {(report.data ?? []).map((r) => (
              <tr key={r.dataClass}>
                <th scope="row">{t(`retention.class.${r.dataClass}`, { defaultValue: r.dataClass })}</th>
                <td>{t('retention.months', { count: r.months })}</td>
                <td dir="ltr">{formatDate(r.cutoff, i18n.language, calendar)}</td>
                <td dir="ltr">
                  {r.eligibleCount === null ? t('retention.notCounted') : formatNumber(r.eligibleCount, i18n.language)}
                  {r.onHold && <span className="badge-inline"> {t('retention.onHold')}</span>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

function Holds({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const holds = useLoad(() => listHolds(true), 'holds')
  const entities = useLoad(listEntities, 'hold-entities')
  const act = useAction()
  const [scope, setScope] = useState<'Tenant' | 'LegalEntity' | 'Vendor'>('Tenant')
  const [entityId, setEntityId] = useState('')
  const [vendorId, setVendorId] = useState('')
  const [reason, setReason] = useState('')
  const [comments, setComments] = useState<Record<string, string>>({})

  const place = async () => {
    const r = await act.run(() => placeHold({ scope, legalEntityId: scope === 'LegalEntity' ? entityId : undefined, vendorId: scope === 'Vendor' ? vendorId.trim() : undefined, reason }))
    if (r) { setReason(''); holds.reload() }
  }
  return (
    <section className="card" aria-labelledby="ret-holds">
      <h2 id="ret-holds">{t('retention.holds')}</h2>
      <p className="muted">{t('retention.holdsNote')}</p>
      <ProblemAlert error={holds.error ?? act.error} />
      {holds.data?.length === 0 && <p>{t('retention.noHolds')}</p>}
      <ul className="plain">
        {(holds.data ?? []).map((h) => (
          <li key={h.id} className="card inner">
            <strong>{t(`retention.scope.${h.scope}`)}</strong> — {h.reason}
            <p className="muted">{formatDateTime(h.placedAt, i18n.language, calendar)}</p>
            <label>{t('erp.comment')}<input value={comments[h.id] ?? ''} maxLength={500} onChange={(e) => setComments({ ...comments, [h.id]: e.target.value })} /></label>
            <button type="button" className="secondary" disabled={act.busy || !(comments[h.id] ?? '').trim()} onClick={() => void act.run(() => releaseHold(h.id, comments[h.id] ?? '')).then((r) => r && holds.reload())}>{t('retention.release')}</button>
          </li>
        ))}
      </ul>
      <form className="stack" onSubmit={(e) => { e.preventDefault(); void place() }}>
        <div className="row">
          <label>{t('retention.scopeLabel')}
            <select value={scope} onChange={(e) => setScope(e.target.value as typeof scope)}>
              {(['Tenant', 'LegalEntity', 'Vendor'] as const).map((s) => (<option key={s} value={s}>{t(`retention.scope.${s}`)}</option>))}
            </select>
          </label>
          {scope === 'LegalEntity' && (
            <label>{t('invoices.entity')}
              <select value={entityId} onChange={(e) => setEntityId(e.target.value)}>
                <option value="">—</option>
                {(entities.data ?? []).map((x) => (<option key={x.id} value={x.id}>{x.code} — {x.legalName}</option>))}
              </select>
            </label>
          )}
          {scope === 'Vendor' && <label>{t('retention.vendorId')}<input dir="ltr" value={vendorId} onChange={(e) => setVendorId(e.target.value)} /></label>}
        </div>
        <label>{t('retention.reason')}<input value={reason} maxLength={500} onChange={(e) => setReason(e.target.value)} /></label>
        <button type="submit" disabled={act.busy || !reason.trim() || (scope === 'LegalEntity' && !entityId) || (scope === 'Vendor' && !vendorId.trim())}>{t('retention.place')}</button>
      </form>
    </section>
  )
}
