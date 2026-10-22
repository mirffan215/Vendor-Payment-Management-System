import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router-dom'
import { CHANNELS, NOTIFICATION_KINDS, getPreferences, listNotifications, markAllNotificationsRead, markNotificationRead, setPreference } from '../../api/notifications'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDateTime, type CalendarPreference } from '../../i18n/format'

export function NotificationsPage({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const [unreadOnly, setUnreadOnly] = useState(false)
  const list = useLoad(() => listNotifications(i18n.language, unreadOnly), `notes-${i18n.language}-${unreadOnly}`)
  const prefs = useLoad(getPreferences, 'prefs')
  const act = useAction()

  return (
    <div className="stack">
      <section className="card" aria-labelledby="notes-title">
        <h1 id="notes-title">{t('notifications.title')}</h1>
        <p>{t('notifications.intro')}</p>
        <ProblemAlert error={list.error ?? act.error} />
        <div className="row">
          <label className="inline">
            <input type="checkbox" checked={unreadOnly} onChange={(e) => setUnreadOnly(e.target.checked)} />
            {t('notifications.unreadOnly')}
          </label>
          <button type="button" className="secondary" disabled={act.busy} onClick={() => void act.run(() => markAllNotificationsRead()).then((r) => r && list.reload())}>
            {t('notifications.markAll')}
          </button>
        </div>
        {list.data?.length === 0 && <p>{t('notifications.empty')}</p>}
        <ul className="plain">
          {(list.data ?? []).map((n) => (
            <li key={n.id} className="card inner">
              <strong>{n.title}</strong>
              {!n.readAt && <span className="badge-inline"> {t('notifications.new')}</span>}
              <p>{n.body}</p>
              <p className="muted">{formatDateTime(n.createdAt, i18n.language, calendar)}</p>
              <div className="row">
                <Link to={n.path} onClick={() => void markNotificationRead(n.id)}>{t('notifications.open')}</Link>
                {!n.readAt && (
                  <button type="button" className="secondary" disabled={act.busy} onClick={() => void act.run(() => markNotificationRead(n.id)).then(() => list.reload())}>
                    {t('notifications.markRead')}
                  </button>
                )}
              </div>
            </li>
          ))}
        </ul>
      </section>

      <section className="card" aria-labelledby="prefs-title">
        <h2 id="prefs-title">{t('notifications.preferencesTitle')}</h2>
        <p>{t('notifications.preferencesIntro')}</p>
        <ProblemAlert error={prefs.error} />
        <div className="table-wrap" tabIndex={0}>
          <table>
            <thead>
              <tr>
                <th scope="col">{t('notifications.kindColumn')}</th>
                {CHANNELS.map((c) => (<th key={c} scope="col">{t(`notifications.channel.${c}`)}</th>))}
              </tr>
            </thead>
            <tbody>
              {NOTIFICATION_KINDS.map((kind) => (
                <tr key={kind}>
                  <th scope="row">{t(`notifications.kind.${kind}`)}</th>
                  {CHANNELS.map((channel) => {
                    const p = prefs.data?.find((x) => x.kind === kind && x.channel === channel)
                    return (
                      <td key={channel}>
                        <input
                          type="checkbox"
                          aria-label={`${t(`notifications.kind.${kind}`)} — ${t(`notifications.channel.${channel}`)}`}
                          checked={p?.enabled ?? true}
                          disabled={!p || p.mandatory || act.busy}
                          onChange={(e) => void act.run(() => setPreference(kind, channel, e.target.checked)).then((r) => r && prefs.reload())}
                        />
                      </td>
                    )
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="muted">{t('notifications.mandatoryNote')}</p>
      </section>
    </div>
  )
}
