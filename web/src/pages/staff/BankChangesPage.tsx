import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { approveBankChange, listBankChanges, rejectBankChange, type BankChangeDto } from '../../api/staff'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDateTime, type CalendarPreference } from '../../i18n/format'

export function BankChangesPage({ calendar }: { calendar: CalendarPreference }) {
  const { t } = useTranslation()
  const queue = useLoad(() => listBankChanges('Pending'), 'pending')
  return (
    <section className="card" aria-labelledby="queue-title">
      <h1 id="queue-title">{t('bank.queueTitle')}</h1>
      <p>{t('bank.queueIntro')}</p>
      <ProblemAlert error={queue.error} />
      {queue.loading && !queue.data && <p role="status">{t('common.loading')}</p>}
      {queue.data?.length === 0 && <p>{t('bank.queueEmpty')}</p>}
      <ul className="plain">
        {(queue.data ?? []).map((c) => (
          <ChangeCard key={c.id} change={c} calendar={calendar} onDecided={queue.reload} />
        ))}
      </ul>
    </section>
  )
}

function ChangeCard({ change: c, calendar, onDecided }: { change: BankChangeDto; calendar: CalendarPreference; onDecided: () => void }) {
  const { t, i18n } = useTranslation()
  const act = useAction()
  const [callback, setCallback] = useState('')
  const [comment, setComment] = useState('')

  const decide = async (fn: () => Promise<unknown>) => {
    if ((await act.run(fn)) !== undefined) onDecided()
  }

  return (
    <li className="card inner">
      <h2>
        <Link to={`/vendors/${c.vendorId}`}>{c.vendorName}</Link>
      </h2>
      <dl>
        <dt>{t('bank.source')}</dt>
        <dd>{t(`bankSource.${c.source}`, { defaultValue: c.source })}</dd>
        <dt>{t('bank.proposed')}</dt>
        <dd>
          {c.proposed.accountName} · {c.proposed.bankName} · <span dir="ltr">{c.proposed.maskedAccount}</span> · <span dir="ltr">{c.proposed.currencyCode}</span>
        </dd>
        <dt>{t('bank.current')}</dt>
        <dd>
          {c.current ? (
            <>
              {c.current.bankName} · <span dir="ltr">{c.current.maskedAccount}</span>
            </>
          ) : (
            t('bank.none')
          )}
        </dd>
        <dt>{t('bank.reason')}</dt>
        <dd>{c.reason}</dd>
        <dt>{t('bank.expires')}</dt>
        <dd>{formatDateTime(c.expiresAt, i18n.language, calendar)}</dd>
      </dl>
      {c.requiresCallback && <p className="alert">{t('bank.callbackNeeded')}</p>}
      <label>
        {t('bank.callbackRef')}
        <input value={callback} onChange={(e) => setCallback(e.target.value)} maxLength={500} />
      </label>
      <label>
        {t('bank.comment')}
        <input value={comment} onChange={(e) => setComment(e.target.value)} maxLength={500} />
      </label>
      <ProblemAlert error={act.error} />
      <div className="row">
        <button type="button" disabled={act.busy} onClick={() => void decide(() => approveBankChange(c.id, callback, comment))}>
          {t('bank.approve')}
        </button>
        <button type="button" disabled={act.busy || !comment.trim()} onClick={() => void decide(() => rejectBankChange(c.id, comment))}>
          {t('bank.reject')}
        </button>
      </div>
    </li>
  )
}
