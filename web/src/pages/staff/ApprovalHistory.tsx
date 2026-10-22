import { useTranslation } from 'react-i18next'
import type { ApprovalRun } from '../../api/approvals'
import { formatDateTime, type CalendarPreference } from '../../i18n/format'

/** Every step of an approval run with who decided what and when, including decisions made for someone else. */
export function ApprovalHistory({ run, calendar }: { run: ApprovalRun; calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  return (
    <div>
      <p>
        {t('approvals.runStatus', { status: t(`approvals.runStatuses.${run.status}`, { defaultValue: run.status }), version: run.definitionVersion })}
        {run.statusReason && <> — {run.statusReason}</>}
      </p>
      {run.needsAttention && <p className="alert">{t('approvals.needsAttention')}</p>}
      <ol className="plain">
        {run.tasks.map((task) => (
          <li key={task.id} className="card inner">
            <strong>{t('approvals.step', { step: task.stepId })}</strong> — {t(`approvals.taskStatuses.${task.status}`, { defaultValue: task.status })}
            {task.dueAt && <> · {t('approvals.due', { when: formatDateTime(task.dueAt, i18n.language, calendar) })}</>}
            {task.escalatedAt && <> · {t('approvals.escalated')}</>}
            <p className="muted">{t('approvals.approvers')}: {task.assigneeNames.map((n, i) => n ?? task.assignees[i]).join(', ') || '—'}</p>
            <ul className="plain">
              {task.decisions.map((d) => (
                <li key={d.id}>
                  {t(`approvals.decision.${d.decision}`, { defaultValue: d.decision })} — {d.actorName ?? d.actorId}
                  {d.actingForId && <> {t('approvals.onBehalfOf', { name: d.actingForName ?? d.actingForId })}</>}
                  {' · '}{formatDateTime(d.decidedAt, i18n.language, calendar)}
                  {d.comment && <> — “{d.comment}”</>}
                </li>
              ))}
            </ul>
          </li>
        ))}
      </ol>
    </div>
  )
}
