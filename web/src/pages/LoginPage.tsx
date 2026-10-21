import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { getProviders, type ProviderSummary } from '../api/client'

export function LoginPage() {
  const { t } = useTranslation()
  const [params] = useSearchParams()
  const [tenant, setTenant] = useState('')
  const [providers, setProviders] = useState<ProviderSummary[] | null>(null)
  const [failed, setFailed] = useState(false)
  const denied = params.get('denied') !== null

  async function find(e: FormEvent) {
    e.preventDefault()
    setFailed(false)
    try {
      setProviders(await getProviders(tenant.trim()))
    } catch {
      setFailed(true)
    }
  }

  return (
    <section aria-labelledby="login-title" className="card narrow">
      <h1 id="login-title">{t('login.title')}</h1>
      {denied && (
        <div role="alert" className="alert">
          <strong>{t('login.deniedTitle')}</strong>
          <p>{t('login.deniedBody')}</p>
        </div>
      )}
      <p>{t('login.intro')}</p>
      <form onSubmit={find}>
        <label htmlFor="tenant">{t('login.tenantLabel')}</label>
        <input
          id="tenant"
          value={tenant}
          onChange={(e) => setTenant(e.target.value)}
          placeholder={t('login.tenantPlaceholder')}
          autoComplete="organization"
          dir="ltr"
          required
        />
        <button type="submit">{t('login.find')}</button>
      </form>
      {failed && <p role="alert">{t('errors.generic')}</p>}
      {providers && providers.length === 0 && <p>{t('login.noProviders')}</p>}
      {providers && providers.length > 0 && (
        <ul className="plain">
          {providers.map((p) => (
            <li key={p.id}>
              <a className="button" href={`/auth/login?provider=${encodeURIComponent(p.id)}&returnUrl=%2F`}>
                {t('login.continueWith', { provider: p.displayName })}
              </a>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
