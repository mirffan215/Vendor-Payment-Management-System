import { useTranslation } from 'react-i18next'
import { useSession } from '../auth/session'

export function HomePage() {
  const { t } = useTranslation()
  const { state } = useSession()
  if (state.status !== 'authenticated') return null
  const { session } = state
  return (
    <section aria-labelledby="home-title" className="card">
      <h1 id="home-title">{t('home.title', { name: session.displayName })}</h1>
      <p>{t('home.session')}</p>
      <dl>
        <dt>{t('home.email')}</dt>
        <dd dir="ltr">{session.email}</dd>
        <dt>{t('home.tenant')}</dt>
        <dd dir="ltr">{session.tenantId}</dd>
      </dl>
    </section>
  )
}
