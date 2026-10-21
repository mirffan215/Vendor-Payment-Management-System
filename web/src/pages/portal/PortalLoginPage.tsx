import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { portalLogin, portalMfa } from '../../api/portal'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'

/** Step 1 password, step 2 authenticator code. Both steps answer failures the same way. */
export function PortalLoginPage({ onSignedIn }: { onSignedIn: () => Promise<void> }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const [f, setF] = useState({ tenant: params.get('tenant') ?? '', email: '', password: '' })
  const [challenge, setChallenge] = useState<string>()
  const [code, setCode] = useState('')
  const act = useAction()

  async function password(e: FormEvent) {
    e.preventDefault()
    const r = await act.run(() => portalLogin(f.tenant.trim(), f.email.trim(), f.password))
    if (r) {
      setF({ ...f, password: '' })
      setChallenge(r.challenge)
    }
  }

  async function mfa(e: FormEvent) {
    e.preventDefault()
    if (!challenge) return
    const ok = await act.run(async () => {
      await portalMfa(challenge, code.trim())
      return true
    })
    if (ok) {
      await onSignedIn()
      navigate('/portal')
    }
  }

  return (
    <section className="card narrow" aria-labelledby="plogin-title">
      <h1 id="plogin-title">{t('portal.signInTitle')}</h1>
      {!challenge ? (
        <form onSubmit={password} className="stack">
          <label>{t('login.tenantLabel')}<input value={f.tenant} onChange={(e) => setF({ ...f, tenant: e.target.value })} required dir="ltr" autoComplete="organization" /></label>
          <label>{t('portal.email')}<input type="email" value={f.email} onChange={(e) => setF({ ...f, email: e.target.value })} required dir="ltr" autoComplete="username" /></label>
          <label>{t('portal.password')}<input type="password" value={f.password} onChange={(e) => setF({ ...f, password: e.target.value })} required autoComplete="current-password" /></label>
          <ProblemAlert error={act.error} />
          <button type="submit" disabled={act.busy}>{t('portal.continue')}</button>
        </form>
      ) : (
        <form onSubmit={mfa} className="stack">
          <p>{t('portal.enterCode')}</p>
          <label>{t('portal.code')}<input value={code} onChange={(e) => setCode(e.target.value)} required autoComplete="one-time-code" inputMode="numeric" dir="ltr" /></label>
          <ProblemAlert error={act.error} />
          <button type="submit" disabled={act.busy}>{t('portal.signIn')}</button>
        </form>
      )}
    </section>
  )
}
