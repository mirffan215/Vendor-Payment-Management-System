import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { listExceptions, waiveException, type ExceptionDto } from '../../api/invoices'
import { useAction } from '../../components/Busy'
import { FindingText, RuleName } from '../../components/Findings'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDateTime, type CalendarPreference } from '../../i18n/format'

export function ExceptionsPage({ calendar }: { calendar: CalendarPreference }) {
  const { t } = useTranslation()
  const queue = useLoad(() => listExceptions('Open'), 'open')
  return (
    <section className="card" aria-labelledby="exc-title">
      <h1 id="exc-title">{t('exceptions.title')}</h1>
      <p>{t('exceptions.intro')}</p>
      <ProblemAlert error={queue.error} />
      {queue.loading && !queue.data && <p role="status">{t('common.loading')}</p>}
      {queue.data?.length === 0 && <p>{t('exceptions.empty')}</p>}
      <ul className="plain">
        {(queue.data ?? []).map((e) => (
          <ExceptionCard key={e.id} exception={e} calendar={calendar} onDone={queue.reload} />
        ))}
      </ul>
    </section>
  )
}

function ExceptionCard({ exception: e, calendar, onDone }: { exception: ExceptionDto; calendar: CalendarPreference; onDone: () => void }) {
  const { t, i18n } = useTranslation()
  const act = useAction()
  const [reason, setReason] = useState('')
  return (
    <li className="card inner">
      <h2>
        <Link to={`/invoices/${e.invoiceId}`} dir="ltr">{e.invoiceNumber || t('invoices.noNumber')}</Link> — <RuleName code={e.ruleCode} />
      </h2>
      <p>
        {t(`severity.${e.severity}`)} · {formatDateTime(e.createdAt, i18n.language, calendar)}
        {e.severity === 'Block' && <> · {t('exceptions.blockNote')}</>}
      </p>
      <ul className="plain">{e.findings.map((f, n) => (<li key={n}><FindingText finding={f} /></li>))}</ul>
      <label>{t('exceptions.waiveReason')}<input value={reason} onChange={(ev) => setReason(ev.target.value)} maxLength={500} /></label>
      <ProblemAlert error={act.error} />
      <button type="button" disabled={act.busy || !reason.trim()} onClick={() => void act.run(() => waiveException(e.id, reason)).then((r) => r && onDone())}>
        {t('exceptions.waive')}
      </button>
    </li>
  )
}
