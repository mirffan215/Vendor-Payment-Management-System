import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { acceptInvite, enrollAuthenticator, getInvite, type Enrollment } from '../../api/portal'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { QrCode } from '../../components/QrCode'
import { useLoad } from '../../hooks/useLoad'

type Step = { name: 'password' } | { name: 'authenticator'; enrollment: Enrollment } | { name: 'codes'; codes: string[] } | { name: 'done' }

/** Invitation link: choose a password, set up the authenticator app, save recovery codes. Failure reasons are never revealed. */
export function AcceptPage({ onSignedIn }: { onSignedIn: () => Promise<void> }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const tenant = params.get('tenant') ?? ''
  const token = params.get('token') ?? ''
  const invite = useLoad(() => getInvite(tenant, token), `${tenant}|${token}`)
  const [step, setStep] = useState<Step>({ name: 'password' })
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const act = useAction()

  if (invite.loading && !invite.data) return <p role="status">{t('common.loading')}</p>
  if (!invite.data) {
    return (
      <section className="card narrow" aria-labelledby="accept-title">
        <h1 id="accept-title">{t('portal.invalidLinkTitle')}</h1>
        <p role="alert">{t('portal.invalidLink')}</p>
      </section>
    )
  }

  async function setPasswordStep(e: FormEvent) {
    e.preventDefault()
    const r = await act.run(() => acceptInvite(tenant, token, password))
    if (!r) return
    setPassword('')
    setStep(r.completed || !r.enrollment ? { name: 'done' } : { name: 'authenticator', enrollment: r.enrollment })
  }

  async function enrol(e: FormEvent) {
    e.preventDefault()
    const r = await act.run(() => enrollAuthenticator(tenant, token, code.trim()))
    if (!r) return
    setStep({ name: 'codes', codes: r.recoveryCodes })
  }

  return (
    <section className="card narrow" aria-labelledby="accept-title">
      <h1 id="accept-title">{t('portal.acceptTitle', { vendor: invite.data.vendorName })}</h1>
      <p>{t('portal.acceptIntro', { name: invite.data.displayName })}</p>

      {step.name === 'password' && (
        <form onSubmit={setPasswordStep} className="stack">
          <label>
            {t('portal.email')}
            <input value={invite.data.email} readOnly dir="ltr" />
          </label>
          <label>
            {t('portal.newPassword')}
            <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} required minLength={12} autoComplete="new-password" />
          </label>
          <p className="muted">{t('portal.passwordRules')}</p>
          <ProblemAlert error={act.error} />
          <button type="submit" disabled={act.busy}>{t('portal.continue')}</button>
        </form>
      )}

      {step.name === 'authenticator' && (
        <form onSubmit={enrol} className="stack">
          <h2>{t('portal.setupAuthenticator')}</h2>
          <p>{t('portal.scanQr')}</p>
          <QrCode value={step.enrollment.provisioningUri} label={t('portal.qrLabel')} />
          <p>{t('portal.orEnterSecret')}</p>
          <code dir="ltr" className="secret">{step.enrollment.secret}</code>
          <label>
            {t('portal.code')}
            <input value={code} onChange={(e) => setCode(e.target.value)} inputMode="numeric" autoComplete="one-time-code" required maxLength={6} dir="ltr" />
          </label>
          <ProblemAlert error={act.error} />
          <button type="submit" disabled={act.busy}>{t('portal.verify')}</button>
        </form>
      )}

      {step.name === 'codes' && (
        <div className="stack">
          <h2>{t('portal.recoveryTitle')}</h2>
          <p>{t('portal.recoveryIntro')}</p>
          <ul className="plain codes" dir="ltr">
            {step.codes.map((c) => (
              <li key={c}><code>{c}</code></li>
            ))}
          </ul>
          <button
            type="button"
            onClick={() => {
              void onSignedIn().then(() => navigate('/portal'))
            }}
          >
            {t('portal.savedCodes')}
          </button>
        </div>
      )}

      {step.name === 'done' && (
        <p>
          <Link to="/portal/login">{t('portal.goToSignIn')}</Link>
        </p>
      )}
    </section>
  )
}
