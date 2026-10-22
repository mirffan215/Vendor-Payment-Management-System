import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router-dom'
import { decideTask, getApprovalTask, getInbox } from '../../api/approvals'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatMoney, type CalendarPreference } from '../../i18n/format'
import { ApprovalHistory } from './ApprovalHistory'

export function ApprovalTaskPage({ calendar }: { calendar: CalendarPreference }) {
  const { taskId = '' } = useParams()
  const { t, i18n } = useTranslation()
  const run = useLoad(() => getApprovalTask(taskId), taskId)
  const inbox = useLoad(getInbox, 'inbox')
  const act = useAction()
  const [comment, setComment] = useState('')

  if (run.loading && !run.data) return <p role="status">{t('common.loading')}</p>
  if (!run.data) return <ProblemAlert error={run.error ?? new Error('load')} />
  const data = run.data
  const open = data.tasks.find((x) => x.id === taskId)?.status === 'Pending' && data.status === 'Active'
  const mine = inbox.data?.find((i) => i.taskId === taskId)

  async function decide(decision: string) {
    if ((await act.run(() => decideTask(taskId, decision, comment, mine?.actingForId ?? null))) !== undefined) {
      setComment('')
      run.reload()
      inbox.reload()
    }
  }

  return (
    <div className="stack">
      <p><Link to="/approvals">{t('approvals.back')}</Link></p>
      <section className="card" aria-labelledby="task-title">
        <h1 id="task-title">{data.subjectLabel}</h1>
        <dl>
          <dt>{t('invoices.total')}</dt>
          <dd>{formatMoney(data.amount, data.currency, i18n.language)}</dd>
          <dt>{t('invoices.version')}</dt>
          <dd>{data.subjectVersion}</dd>
        </dl>
        <p><Link to={`/invoices/${data.subjectId}`}>{t('approvals.viewInvoice')}</Link></p>
        {mine?.actingForName && <p className="muted">{t('approvals.actingFor', { name: mine.actingForName })}</p>}
        {open ? (
          <div className="stack">
            <label>
              {t('approvals.comment')}
              <textarea value={comment} onChange={(e) => setComment(e.target.value)} maxLength={1000} rows={3} />
            </label>
            <p className="muted">{t('approvals.commentHelp')}</p>
            <ProblemAlert error={act.error} />
            <div className="row">
              <button type="button" disabled={act.busy} onClick={() => void decide('Approve')}>{t('approvals.approve')}</button>
              <button type="button" className="secondary" disabled={act.busy || !comment.trim()} onClick={() => void decide('Return')}>{t('approvals.return')}</button>
              <button type="button" className="secondary" disabled={act.busy || !comment.trim()} onClick={() => void decide('Reject')}>{t('approvals.reject')}</button>
            </div>
          </div>
        ) : (
          <p role="status">{t('approvals.closed')}</p>
        )}
      </section>
      <section className="card" aria-labelledby="task-history">
        <h2 id="task-history">{t('approvals.history')}</h2>
        <ApprovalHistory run={data} calendar={calendar} />
      </section>
    </div>
  )
}
