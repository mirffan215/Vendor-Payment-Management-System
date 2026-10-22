import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { cancelRequest, createProposals, createRequests, getPayable, getProposals, getRequests, type RequestResult } from '../../api/payments'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDate, formatMoney, type CalendarPreference } from '../../i18n/format'

export function PaymentsPage({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const payable = useLoad(() => getPayable(), 'payable')
  const requests = useLoad(() => getRequests(), 'requests')
  const proposals = useLoad(() => getProposals(), 'proposals')
  const act = useAction()
  const [picked, setPicked] = useState<string[]>([])
  const [results, setResults] = useState<RequestResult[]>()
  const [reason, setReason] = useState('')

  const reloadAll = () => {
    payable.reload()
    requests.reload()
    proposals.reload()
  }
  const toggle = (id: string) => setPicked(picked.includes(id) ? picked.filter((x) => x !== id) : [...picked, id])
  const available = (payable.data ?? []).filter((p) => !p.hasOpenRequest)

  async function create() {
    const r = await act.run(() => createRequests(picked))
    if (r) {
      setResults(r)
      setPicked([])
      reloadAll()
    }
  }

  const ready = (requests.data ?? []).filter((r) => r.status === 'Created')
  const entities = [...new Set(ready.map((r) => r.legalEntityId))]

  return (
    <div className="stack">
      <section className="card" aria-labelledby="pay-title">
        <h1 id="pay-title">{t('payments.title')}</h1>
        <p>{t('payments.intro')}</p>
        <p className="muted">{t('payments.noTransfer')}</p>
        <ProblemAlert error={payable.error ?? requests.error ?? act.error} />

        <h2>{t('payments.payableTitle')}</h2>
        {payable.data && available.length === 0 && <p>{t('payments.payableEmpty')}</p>}
        <ul className="plain">
          {available.map((p) => (
            <li key={p.invoiceId} className="row">
              <label className="inline">
                <input type="checkbox" checked={picked.includes(p.invoiceId)} disabled={p.blockers.length > 0} onChange={() => toggle(p.invoiceId)} />
                <Link to={`/invoices/${p.invoiceId}`} dir="ltr">{p.invoiceNumber}</Link> — {p.vendorName}
              </label>
              <span>{formatMoney(p.total, p.currencyCode, i18n.language)}</span>
              <span>{t('payments.dueOn', { date: formatDate(p.dueDate, i18n.language, calendar) })}</span>
              {p.blockers.map((b) => (<strong key={b} className="alert">{t(`payments.blocker.${b}`, { defaultValue: b })}</strong>))}
            </li>
          ))}
        </ul>
        <button type="button" disabled={act.busy || picked.length === 0} onClick={() => void create()}>{t('payments.createRequests', { count: picked.length })}</button>
        {results && (
          <ul className="plain" role="status">
            {results.filter((r) => r.errorCode).map((r) => (<li key={r.invoiceId}>{t(`errors.codes.${r.errorCode}`, { defaultValue: r.errorCode })}</li>))}
            {results.every((r) => !r.errorCode) && <li>{t('payments.requestsCreated', { count: results.length })}</li>}
          </ul>
        )}
      </section>

      <section className="card" aria-labelledby="req-title">
        <h2 id="req-title">{t('payments.requestsTitle')}</h2>
        {requests.data?.length === 0 && <p>{t('payments.requestsEmpty')}</p>}
        <label>{t('payments.cancelReason')}<input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} /></label>
        <ul className="plain">
          {(requests.data ?? []).map((r) => (
            <li key={r.id} className="row">
              <span dir="ltr">{r.invoiceNumber}</span> — {r.vendorName}
              <span>{formatMoney(r.amount, r.currencyCode, i18n.language)}</span>
              <span>{t(`payments.requestStatus.${r.status}`, { defaultValue: r.status })}</span>
              {r.statusReason && <span className="muted">{r.statusReason}</span>}
              {r.status === 'Created' && (
                <button type="button" className="secondary" disabled={act.busy || !reason.trim()} onClick={() => void act.run(() => cancelRequest(r.id, reason)).then((x) => x && reloadAll())}>
                  {t('payments.cancelRequest')}
                </button>
              )}
            </li>
          ))}
        </ul>
        {entities.map((entityId) => (
          <button key={entityId} type="button" disabled={act.busy} onClick={() => void act.run(() => createProposals(entityId, null, null)).then((x) => x && reloadAll())}>
            {t('payments.createProposals')}
          </button>
        ))}
      </section>

      <section className="card" aria-labelledby="prop-title">
        <h2 id="prop-title">{t('payments.proposalsTitle')}</h2>
        {proposals.data?.length === 0 && <p>{t('payments.proposalsEmpty')}</p>}
        <ul className="plain">
          {(proposals.data ?? []).map((p) => (
            <li key={p.id} className="row">
              <Link to={`/payments/proposals/${p.id}`} dir="ltr">{p.proposalNo}</Link> — {p.vendorName}
              <span>{formatMoney(p.totalAmount, p.currencyCode, i18n.language)}</span>
              <span>{t(`payments.proposalStatus.${p.status}`, { defaultValue: p.status })}</span>
            </li>
          ))}
        </ul>
      </section>
    </div>
  )
}
