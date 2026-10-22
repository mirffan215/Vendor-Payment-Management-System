import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router-dom'
import {
  getAuthorizations, getInstruction, getProposal, getRecheck, instructionUrl, proposalAction, removeProposalRequest, verifyInstruction,
  type Instruction,
} from '../../api/payments'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDate, formatDateTime, formatMoney, type CalendarPreference } from '../../i18n/format'

export function ProposalDetailPage({ calendar }: { calendar: CalendarPreference }) {
  const { id = '' } = useParams()
  const { t, i18n } = useTranslation()
  const proposal = useLoad(() => getProposal(id), id)
  const live = proposal.data && ['Draft', 'PendingApproval', 'Approved'].includes(proposal.data.status)
  const recheck = useLoad(() => (live ? getRecheck(id) : Promise.resolve(undefined)), `${id}-${proposal.data?.status}`)
  const authorizations = useLoad(() => getAuthorizations(id), `${id}-${proposal.data?.status}-a`)
  const instruction = useLoad(() => (proposal.data?.status === 'Released' ? getInstruction(id) : Promise.resolve(undefined)), `${id}-${proposal.data?.status}-i`)
  const act = useAction()
  const [comment, setComment] = useState('')
  const [verified, setVerified] = useState<boolean>()

  if (proposal.loading && !proposal.data) return <p role="status">{t('common.loading')}</p>
  if (!proposal.data) return <ProblemAlert error={proposal.error ?? new Error('load')} />
  const p = proposal.data
  const run = async (fn: () => Promise<unknown>) => {
    if ((await act.run(fn)) !== undefined) {
      setComment('')
      proposal.reload()
      authorizations.reload()
    }
  }
  const act2 = (action: 'submit' | 'approve' | 'send-back' | 'cancel' | 'release') => run(() => proposalAction(id, action, action === 'approve' ? { comment } : { reason: comment }))

  return (
    <div className="stack">
      <p><Link to="/payments">{t('payments.back')}</Link></p>
      <section className="card" aria-labelledby="pd-title">
        <h1 id="pd-title" dir="ltr">{p.proposalNo}</h1>
        <dl>
          <dt>{t('vendors.status')}</dt>
          <dd>{t(`payments.proposalStatus.${p.status}`, { defaultValue: p.status })}</dd>
          <dt>{t('invoices.vendor')}</dt>
          <dd>{p.vendorName}</dd>
          <dt>{t('payments.dueLabel')}</dt>
          <dd>{formatDate(p.dueDate, i18n.language, calendar)}</dd>
          <dt>{t('invoices.total')}</dt>
          <dd>{formatMoney(p.totalAmount, p.currencyCode, i18n.language)}</dd>
          {p.statusReason && (<><dt>{t('vendors.reason')}</dt><dd>{p.statusReason}</dd></>)}
        </dl>
        <ul className="plain">
          {p.items.map((i) => (
            <li key={i.paymentRequestId} className="row">
              <Link to={`/invoices/${i.invoiceId}`} dir="ltr">{i.invoiceNumber}</Link>
              <span>{formatMoney(i.amount, p.currencyCode, i18n.language)}</span>
              {p.status === 'Draft' && (
                <button type="button" className="secondary" disabled={act.busy} onClick={() => void run(() => removeProposalRequest(id, i.paymentRequestId))}>{t('payments.removeItem')}</button>
              )}
            </li>
          ))}
        </ul>
      </section>

      {live && recheck.data && (
        <section className="card" aria-labelledby="pd-recheck">
          <h2 id="pd-recheck">{t('payments.recheckTitle')}</h2>
          {recheck.data.passed ? <p role="status">{t('payments.recheckPassed')}</p> : (
            <ul className="plain alert">
              {recheck.data.findings.map((f, n) => (
                <li key={n}>{t(`payments.recheck.${f.code}`, { defaultValue: f.code })} ({t(`payments.severity.${f.severity}`)})</li>
              ))}
            </ul>
          )}
        </section>
      )}

      {live && (
        <section className="card" aria-labelledby="pd-actions">
          <h2 id="pd-actions">{t('payments.actions')}</h2>
          <p className="muted">{t('payments.separationNote')}</p>
          <label>{t('payments.comment')}<input value={comment} onChange={(e) => setComment(e.target.value)} maxLength={500} /></label>
          <ProblemAlert error={act.error} />
          <div className="row">
            {p.status === 'Draft' && <button type="button" disabled={act.busy} onClick={() => void act2('submit')}>{t('payments.submit')}</button>}
            {p.status === 'PendingApproval' && <button type="button" disabled={act.busy} onClick={() => void act2('approve')}>{t('payments.approve')}</button>}
            {p.status === 'PendingApproval' && <button type="button" className="secondary" disabled={act.busy || !comment.trim()} onClick={() => void act2('send-back')}>{t('payments.sendBack')}</button>}
            {p.status === 'Approved' && <button type="button" disabled={act.busy} onClick={() => void act2('release')}>{t('payments.release')}</button>}
            <button type="button" className="secondary" disabled={act.busy || !comment.trim()} onClick={() => void act2('cancel')}>{t('payments.cancel')}</button>
          </div>
        </section>
      )}

      {instruction.data && <InstructionPanel instruction={instruction.data} calendar={calendar} verified={verified} onVerify={async () => setVerified((await verifyInstruction(instruction.data!.id)).valid)} onDownloaded={instruction.reload} />}

      {authorizations.data && authorizations.data.length > 0 && (
        <section className="card" aria-labelledby="pd-auth">
          <h2 id="pd-auth">{t('payments.attempts')}</h2>
          <ul className="plain">
            {authorizations.data.map((a) => (
              <li key={a.id}>
                {t(`payments.decision.${a.decision}`)} · {formatDateTime(a.authorizedAt, i18n.language, calendar)}
                {a.separation.map((s) => (<strong key={s.code} className="alert"> {t(`payments.separation.${s.code}`, { defaultValue: s.code })}</strong>))}
                {a.recheck.map((f, n) => (<strong key={n} className="alert"> {t(`payments.recheck.${f.code}`, { defaultValue: f.code })}</strong>))}
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  )
}

function InstructionPanel({ instruction: i, calendar, verified, onVerify, onDownloaded }: {
  instruction: Instruction; calendar: CalendarPreference; verified: boolean | undefined; onVerify: () => Promise<void>; onDownloaded: () => void
}) {
  const { t, i18n } = useTranslation()
  const act = useAction()
  return (
    <section className="card" aria-labelledby="pd-instr">
      <h2 id="pd-instr">{t('payments.instructionTitle')}</h2>
      <p>{t('payments.instructionNote')}</p>
      <dl>
        <dt>{t('vendors.status')}</dt>
        <dd>{t(`payments.instructionStatus.${i.status}`, { defaultValue: i.status })}</dd>
        <dt>{t('payments.prepared')}</dt>
        <dd>{formatDateTime(i.preparedAt, i18n.language, calendar)}</dd>
        <dt>{t('payments.items')}</dt>
        <dd>{i.itemCount} · {formatMoney(i.totalAmount, i.currencyCode, i18n.language)}</dd>
        <dt>{t('payments.fingerprint')}</dt>
        <dd dir="ltr"><code>{i.hash.slice(0, 16)}…</code></dd>
      </dl>
      <div className="row">
        <a href={instructionUrl(i.id, 'csv')} onClick={() => setTimeout(onDownloaded, 800)}>{t('payments.downloadCsv')}</a>
        <a href={instructionUrl(i.id, 'json')} onClick={() => setTimeout(onDownloaded, 800)}>{t('payments.downloadJson')}</a>
        <button type="button" className="secondary" disabled={act.busy} onClick={() => void act.run(onVerify)}>{t('payments.verify')}</button>
      </div>
      <ProblemAlert error={act.error} />
      {verified === true && <p role="status">{t('payments.verified')}</p>}
      {verified === false && <p role="alert" className="alert">{t('payments.notVerified')}</p>}
    </section>
  )
}
