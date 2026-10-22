import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  approveReconAcceptance, getReconSummary, listReconItems, proposeReconAcceptance, reconExportUrl, rejectReconAcceptance, resolveReconItem, RECON_TYPES, runReconciliation,
  type ReconItem, type ReconRun,
} from '../../api/erp'
import { listEntities } from '../../api/staff'
import { useAction } from '../../components/Busy'
import { ErpNav } from '../../components/ErpNav'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDateTime, type CalendarPreference } from '../../i18n/format'

const STATUSES = ['Open', 'PendingAcceptance', 'Accepted', 'Resolved', 'Cleared'] as const

export function ErpReconciliationPage({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const entities = useLoad(listEntities, 'entities')
  const [entityId, setEntityId] = useState('')
  const [status, setStatus] = useState('Open')
  const [type, setType] = useState('')
  const [run, setRun] = useState<ReconRun>()
  const summary = useLoad(getReconSummary, 'recon-summary')
  const items = useLoad(() => listReconItems(status || undefined, type || undefined), `recon-${status}-${type}`)
  const act = useAction()
  const [reasons, setReasons] = useState<Record<string, string>>({})
  const current = entityId || entities.data?.[0]?.id || ''
  const entityName = (id: string) => entities.data?.find((e) => e.id === id)?.code ?? id

  const reloadAll = () => {
    summary.reload()
    items.reload()
  }
  const reasonOf = (item: ReconItem) => reasons[item.id] ?? ''
  const decide = async (item: ReconItem, action: (id: string, text: string) => Promise<ReconItem>) => {
    const r = await act.run(() => action(item.id, reasonOf(item)))
    if (r) reloadAll()
  }

  return (
    <div className="stack erp">
      <ErpNav />
      <section className="card" aria-labelledby="recon-title">
        <h1 id="recon-title">{t('erp.recon.title')}</h1>
        <p>{t('erp.recon.intro')}</p>
        <p className="muted">{t('erp.recon.twoPeople')}</p>
        <ProblemAlert error={entities.error ?? summary.error ?? items.error ?? act.error} />
        <div className="row">
          <label>
            {t('invoices.entity')}
            <select value={current} onChange={(e) => setEntityId(e.target.value)}>
              {(entities.data ?? []).map((x) => (<option key={x.id} value={x.id}>{x.code} — {x.legalName}</option>))}
            </select>
          </label>
          <button type="button" disabled={act.busy || !current} onClick={() => void act.run(() => runReconciliation(current)).then((r) => { if (r) { setRun(r); reloadAll() } })}>
            {t('erp.recon.run')}
          </button>
          <a href={reconExportUrl(status || undefined)} download>{t('erp.recon.export')}</a>
        </div>
        {run && (
          <p role="status" className={run.status === 'Failed' ? 'alert' : undefined}>
            {run.status === 'Failed'
              ? t('erp.recon.runFailed', { code: t(`erp.testError.${run.errorCode}`, { defaultValue: run.errorCode ?? '' }) })
              : t('erp.recon.runDone', { vpms: run.vpmsInvoices, erp: run.erpDocuments, open: run.openItems })}
          </p>
        )}
        {summary.data && (
          <div className="tiles">
            <div><strong>{summary.data.open}</strong>{t('erp.recon.summary.open')}</div>
            <div><strong>{summary.data.pendingAcceptance}</strong>{t('erp.recon.summary.pending')}</div>
            <div><strong>{summary.data.accepted}</strong>{t('erp.recon.summary.accepted')}</div>
            <div><strong>{summary.data.resolved}</strong>{t('erp.recon.summary.resolved')}</div>
          </div>
        )}
      </section>

      <section className="card" aria-labelledby="recon-items-title">
        <h2 id="recon-items-title">{t('erp.recon.itemsTitle')}</h2>
        <div className="row">
          <label>
            {t('erp.recon.statusFilter')}
            <select value={status} onChange={(e) => setStatus(e.target.value)}>
              <option value="">{t('erp.sync.all')}</option>
              {STATUSES.map((s) => (<option key={s} value={s}>{t(`erp.recon.status.${s}`)}</option>))}
            </select>
          </label>
          <label>
            {t('erp.recon.typeFilter')}
            <select value={type} onChange={(e) => setType(e.target.value)}>
              <option value="">{t('erp.sync.all')}</option>
              {RECON_TYPES.map((x) => (<option key={x} value={x}>{t(`erp.recon.type.${x}`)}</option>))}
            </select>
          </label>
        </div>
        {items.data?.length === 0 && <p>{t('erp.recon.empty')}</p>}
        <ul className="plain">
          {(items.data ?? []).map((item) => (
            <li key={item.id} className="card inner">
              <div className="row">
                <strong>{t(`erp.recon.type.${item.type}`)}</strong>
                <span dir="ltr">{entityName(item.legalEntityId)}</span>
                {item.invoiceNumber && <span dir="ltr">{item.invoiceNumber}</span>}
                <span>{t(`erp.recon.status.${item.status}`)}</span>
              </div>
              <div className="row">
                <span>{t('erp.recon.vpmsValue')}: <bdi>{item.vpmsValue}</bdi></span>
                <span>{t('erp.recon.erpValue')}: <bdi>{item.erpValue}</bdi></span>
              </div>
              <p className="muted">{t('erp.recon.lastSeen', { date: formatDateTime(item.lastSeenAt, i18n.language, calendar) })}</p>
              {item.proposedReason && item.status === 'PendingAcceptance' && <p>{t('erp.recon.proposed', { reason: item.proposedReason })}</p>}
              {item.decisionReason && <p>{item.decisionReason}</p>}
              {(item.status === 'Open' || item.status === 'PendingAcceptance') && (
                <div className="stack">
                  <label>{t('erp.sync.reason')}<input value={reasonOf(item)} maxLength={500} onChange={(e) => setReasons({ ...reasons, [item.id]: e.target.value })} /></label>
                  <div className="row">
                    {item.status === 'Open' && (
                      <>
                        <button type="button" disabled={act.busy || !reasonOf(item).trim()} onClick={() => void decide(item, resolveReconItem)}>{t('erp.recon.resolve')}</button>
                        <button type="button" className="secondary" disabled={act.busy || !reasonOf(item).trim()} onClick={() => void decide(item, proposeReconAcceptance)}>{t('erp.recon.propose')}</button>
                      </>
                    )}
                    {item.status === 'PendingAcceptance' && (
                      <>
                        <button type="button" disabled={act.busy} onClick={() => void decide(item, approveReconAcceptance)}>{t('erp.recon.approve')}</button>
                        <button type="button" className="secondary" disabled={act.busy || !reasonOf(item).trim()} onClick={() => void decide(item, rejectReconAcceptance)}>{t('erp.recon.reject')}</button>
                      </>
                    )}
                  </div>
                </div>
              )}
            </li>
          ))}
        </ul>
      </section>
    </div>
  )
}
