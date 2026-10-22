import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useLocation } from 'react-router-dom'
import { getUnreadCount } from '../api/notifications'

/** A link to the notification inbox with the number of unread messages. It refreshes on every page change and once a minute. */
export function NotificationBell() {
  const { t } = useTranslation()
  const location = useLocation()
  const [count, setCount] = useState(0)

  useEffect(() => {
    let cancelled = false
    const load = () => getUnreadCount().then((r) => { if (!cancelled) setCount(r.count) }).catch(() => undefined)
    load()
    const timer = window.setInterval(load, 60_000)
    return () => {
      cancelled = true
      window.clearInterval(timer)
    }
  }, [location.pathname])

  return (
    <Link to="/notifications" className="bell" aria-label={t('notifications.bell', { count })}>
      {t('nav.notifications')}
      {count > 0 && <span className="badge" aria-hidden="true">{count > 99 ? '99+' : count}</span>}
    </Link>
  )
}
