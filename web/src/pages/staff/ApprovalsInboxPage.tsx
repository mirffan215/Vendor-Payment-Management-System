import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { getInbox, getNotices, markNoticeRead } from '../../api/approvals'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDateTime, formatMoney, type CalendarPreference } from '../../i18n/format'

export function ApprovalsInboxPage({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const inbox = useLoad(getInbox, 'inbox')
  const notices = useLoad(getNotices, 'notices')
  const act = useAction()

  return (
    <div className="stack">
      <section className="card" aria-labelledby="appr-title">
        <h1 id="appr-title">{t('approvals.title')}</h1>
        <p>{t('approvals.intro')}</p>
        <ProblemAlert error={inbox.error} />
        {inbox.loading && !inbox.data && <p role="status">{t('common.loading')}</p>}
        {inbox.data?.length === 0 && <p>{t('approvals.empty')}</p>}
        <ul className="plain">
          {(inbox.data ?? []).map((item) => (
            <li key={item.taskId} className="card inner">
              <h2>
                <Link to={`/approvals/${item.taskId}`}>{item.subjectLabel}</Link>
              </h2>
              <p>
                {formatMoney(item.amount, item.currency, i18n.language)} · {t('approvals.step', { step: item.stepId })} ·{' '}
                {t(`approvals.mode.${item.mode}`, { defaultValue: item.mode })}
                {item.mode !== 'AnyOne' && <> ({item.approvalsSoFar}/{item.approvers})</>}
              </p>
              <p>
                {item.dueAt ? t('approvals.due', { when: formatDateTime(item.dueAt, i18n.language, calendar) }) : t('approvals.noDeadline')}
                {item.overdue && <strong> · {t('approvals.overdue')}</strong>}
                {item.escalated && <> · {t('approvals.escalated')}</>}
              </p>
              {item.actingForName && <p className="muted">{t('approvals.actingFor', { name: item.actingForName })}</p>}
            </li>
          ))}
        </ul>
      </section>

      <section className="card" aria-labelledby="notice-title">
        <h2 id="notice-title">{t('approvals.noticesTitle')}</h2>
        <ProblemAlert error={act.error ?? notices.error} />
        {notices.data?.length === 0 && <p>{t('approvals.noNotices')}</p>}
        <ul className="plain">
          {(notices.data ?? []).map((n) => (
            <li key={n.id} className="row">
              <span>{t(`approvals.notice.${n.kind}`, { label: n.subjectLabel })}</span>
              <span className="muted">{formatDateTime(n.createdAt, i18n.language, calendar)}</span>
              <Link to={`/approvals/${n.taskId}`}>{t('approvals.open')}</Link>
              {!n.readAt && (
                <button type="button" className="secondary" disabled={act.busy} onClick={() => void act.run(() => markNoticeRead(n.id)).then(() => notices.reload())}>
                  {t('approvals.markRead')}
                </button>
              )}
            </li>
          ))}
        </ul>
      </section>
    </div>
  )
}
