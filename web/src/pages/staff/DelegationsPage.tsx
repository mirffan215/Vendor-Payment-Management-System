import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { createDelegation, getApprovers, getDelegations, revokeDelegation } from '../../api/approvals'
import { listEntities } from '../../api/staff'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDateTime, type CalendarPreference } from '../../i18n/format'

export function DelegationsPage({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const entities = useLoad(listEntities, 'entities')
  const delegations = useLoad(() => getDelegations(false), 'delegations')
  const act = useAction()
  const [entityId, setEntityId] = useState('')
  const [delegateId, setDelegateId] = useState('')
  const [from, setFrom] = useState(() => new Date().toISOString().slice(0, 10))
  const [to, setTo] = useState('')
  const [cap, setCap] = useState('')
  const [reason, setReason] = useState('')
  const entity = entityId || entities.data?.[0]?.id || ''
  const approvers = useLoad(() => (entity ? getApprovers(entity) : Promise.resolve([])), entity)

  async function submit(e: FormEvent) {
    e.preventDefault()
    const saved = await act.run(() => createDelegation({
      legalEntityId: entity, delegateId, validFrom: new Date(from).toISOString(), validTo: new Date(to).toISOString(),
      maxAmount: cap ? Number(cap) : null, reason,
    }))
    if (saved) {
      setReason('')
      delegations.reload()
    }
  }

  return (
    <div className="stack">
      <section className="card" aria-labelledby="del-title">
        <h1 id="del-title">{t('delegations.title')}</h1>
        <p>{t('delegations.intro')}</p>
        <ProblemAlert error={entities.error ?? approvers.error} />
        <form onSubmit={submit} className="stack">
          <label>
            {t('invoices.entity')}
            <select value={entity} onChange={(e) => setEntityId(e.target.value)}>
              {(entities.data ?? []).map((x) => (<option key={x.id} value={x.id}>{x.code} — {x.legalName}</option>))}
            </select>
          </label>
          <label>
            {t('delegations.delegate')}
            <select value={delegateId} onChange={(e) => setDelegateId(e.target.value)} required>
              <option value="">—</option>
              {(approvers.data ?? []).map((a) => (<option key={a.id} value={a.id}>{a.name}</option>))}
            </select>
          </label>
          <div className="row">
            <label>{t('delegations.from')}<input type="date" value={from} onChange={(e) => setFrom(e.target.value)} required /></label>
            <label>{t('delegations.to')}<input type="date" value={to} onChange={(e) => setTo(e.target.value)} required /></label>
            <label>{t('delegations.cap')}<input type="number" min="0" step="any" dir="ltr" value={cap} onChange={(e) => setCap(e.target.value)} /></label>
          </div>
          <p className="muted">{t('delegations.capHelp')}</p>
          <label>{t('delegations.reason')}<input value={reason} onChange={(e) => setReason(e.target.value)} required maxLength={300} /></label>
          <ProblemAlert error={act.error} />
          <button type="submit" disabled={act.busy || !delegateId || !to || !reason.trim()}>{t('delegations.create')}</button>
        </form>
      </section>

      <section className="card" aria-labelledby="del-list">
        <h2 id="del-list">{t('delegations.listTitle')}</h2>
        <ProblemAlert error={delegations.error} />
        {delegations.data?.length === 0 && <p>{t('delegations.empty')}</p>}
        <ul className="plain">
          {(delegations.data ?? []).map((d) => (
            <li key={d.id} className="card inner">
              <p>
                {t('delegations.line', { from: d.delegatorName ?? d.delegatorId, to: d.delegateName ?? d.delegateId })} ·{' '}
                {formatDateTime(d.validFrom, i18n.language, calendar)} → {formatDateTime(d.validTo, i18n.language, calendar)}
                {d.maxAmount !== null && <> · {t('delegations.upTo', { amount: d.maxAmount })}</>} · {t(`delegations.status.${d.status}`, { defaultValue: d.status })}
              </p>
              <p className="muted">{d.reason}</p>
              {d.status === 'Active' && (
                <button type="button" className="secondary" disabled={act.busy} onClick={() => void act.run(() => revokeDelegation(d.id)).then((r) => r && delegations.reload())}>
                  {t('delegations.revoke')}
                </button>
              )}
            </li>
          ))}
        </ul>
      </section>
    </div>
  )
}
