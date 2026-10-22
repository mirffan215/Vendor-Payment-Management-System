import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router-dom'
import {
  createVendorRequest, getVendor, inviteVendorUser, listVendorBank, listVendorRequests, proposeBank, rejectDocument,
  vendorAction, verifyDocument,
} from '../../api/staff'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDate, type CalendarPreference } from '../../i18n/format'

/** Which lifecycle actions make sense in each status (the server enforces the real rules, including maker-checker). */
const ACTIONS: Record<string, { action: string; needsReason?: boolean }[]> = {
  Invited: [{ action: 'submit' }],
  Submitted: [{ action: 'start-review' }, { action: 'reject', needsReason: true }],
  UnderReview: [{ action: 'approve' }, { action: 'reject', needsReason: true }],
  Active: [{ action: 'suspend', needsReason: true }],
  Suspended: [{ action: 'reactivate' }],
  Rejected: [{ action: 'reopen' }],
}

export function VendorDetailPage({ calendar }: { calendar: CalendarPreference }) {
  const { id = '' } = useParams()
  const { t, i18n } = useTranslation()
  const details = useLoad(() => getVendor(id), id)
  const bank = useLoad(() => listVendorBank(id), id)
  const requests = useLoad(() => listVendorRequests(id), id)
  const act = useAction()
  const [reason, setReason] = useState('')

  if (details.loading && !details.data) return <p role="status">{t('common.loading')}</p>
  if (!details.data) return <ProblemAlert error={details.error ?? new Error('load')} />
  const { vendor, documents, links } = details.data

  const run = async (fn: () => Promise<unknown>) => {
    if ((await act.run(fn)) !== undefined) {
      setReason('')
      details.reload()
      bank.reload()
      requests.reload()
    }
  }

  return (
    <div className="stack">
      <p>
        <Link to="/vendors">{t('vendors.back')}</Link>
      </p>
      <section className="card" aria-labelledby="vendor-title">
        <h1 id="vendor-title">{vendor.legalName}</h1>
        <dl>
          <dt>{t('vendors.status')}</dt>
          <dd>{t(`status.${vendor.status}`, { defaultValue: vendor.status })}</dd>
          <dt>{t('vendors.country')}</dt>
          <dd dir="ltr">{vendor.countryCode}</dd>
          <dt>{t('vendors.taxId')}</dt>
          <dd dir="ltr">{vendor.taxId ?? '—'}</dd>
          <dt>{t('vendors.created')}</dt>
          <dd>{formatDate(vendor.createdAt, i18n.language, calendar)}</dd>
          {vendor.statusReason && (
            <>
              <dt>{t('vendors.reason')}</dt>
              <dd>{vendor.statusReason}</dd>
            </>
          )}
        </dl>
        <ProblemAlert error={act.error} />
        <div className="row">
          {(ACTIONS[vendor.status] ?? []).some((a) => a.needsReason) && (
            <label>
              {t('vendors.reason')}
              <input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} />
            </label>
          )}
          {(ACTIONS[vendor.status] ?? []).map((a) => (
            <button
              key={a.action}
              type="button"
              disabled={act.busy || (a.needsReason && !reason.trim())}
              onClick={() => void run(() => vendorAction(id, a.action, a.needsReason ? reason : undefined))}
            >
              {t(`vendors.actions.${a.action}`)}
            </button>
          ))}
        </div>
      </section>

      <section className="card" aria-labelledby="docs-title">
        <h2 id="docs-title">{t('vendors.documents')}</h2>
        {documents.length === 0 && <p>{t('vendors.noDocuments')}</p>}
        <ul className="plain">
          {documents.map((d) => (
            <li key={d.id} className="row">
              <span>
                {t(`documentType.${d.type}`, { defaultValue: d.type })} — <span dir="ltr">{d.fileName}</span>
              </span>
              <span>
                {t(`scan.${d.scanStatus}`, { defaultValue: d.scanStatus })} · {t(`docStatus.${d.status}`, { defaultValue: d.status })}
              </span>
              {d.status === 'Uploaded' && d.scanStatus === 'Clean' && (
                <>
                  <button type="button" disabled={act.busy} onClick={() => void run(() => verifyDocument(id, d.id))}>
                    {t('vendors.verifyDocument')}
                  </button>
                  <button type="button" disabled={act.busy || !reason.trim()} onClick={() => void run(() => rejectDocument(id, d.id, reason))}>
                    {t('vendors.rejectDocument')}
                  </button>
                </>
              )}
            </li>
          ))}
        </ul>
      </section>

      <section className="card" aria-labelledby="links-title">
        <h2 id="links-title">{t('vendors.entities')}</h2>
        {links.length === 0 && <p>{t('vendors.noEntities')}</p>}
        <ul className="plain">
          {links.map((l) => (
            <li key={l.id}>
              <span dir="ltr">{l.legalEntityId}</span> · <span dir="ltr">{l.defaultCurrency}</span> · {t(`status.${l.status}`, { defaultValue: l.status })}
            </li>
          ))}
        </ul>
      </section>

      <section className="card" aria-labelledby="bank-title">
        <h2 id="bank-title">{t('bank.title')}</h2>
        <p>{t('bank.twoPersonNote')}</p>
        <ProblemAlert error={bank.error} />
        <ul className="plain">
          {(bank.data ?? []).map((b) => (
            <li key={b.id}>
              {b.bankName} · <span dir="ltr">{b.maskedAccount}</span> · <span dir="ltr">{b.currencyCode}</span> · {t(`bankStatus.${b.status}`, { defaultValue: b.status })}
            </li>
          ))}
        </ul>
        <ProposeBankForm vendorId={id} onDone={() => { bank.reload() }} />
      </section>

      <section className="card" aria-labelledby="portal-title">
        <h2 id="portal-title">{t('portalAdmin.title')}</h2>
        <InviteForm vendorId={id} />
      </section>

      <section className="card" aria-labelledby="req-title">
        <h2 id="req-title">{t('vendors.requests')}</h2>
        <ul className="plain">
          {(requests.data ?? []).map((r) => (
            <li key={r.id}>
              <strong>{r.subject}</strong> — {t(`requestStatus.${r.status}`, { defaultValue: r.status })}
              <ul className="plain">
                {r.messages.map((m) => (
                  <li key={m.id}>
                    {t(`direction.${m.direction}`, { defaultValue: m.direction })}: {m.body}
                  </li>
                ))}
              </ul>
            </li>
          ))}
        </ul>
        <NewRequestForm vendorId={id} onDone={() => requests.reload()} />
      </section>
    </div>
  )
}

function ProposeBankForm({ vendorId, onDone }: { vendorId: string; onDone: () => void }) {
  const { t } = useTranslation()
  const act = useAction()
  const [f, setF] = useState({ accountName: '', bankName: '', swiftBic: '', countryCode: 'SA', currencyCode: 'SAR', iban: '', reason: '' })
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value })

  async function submit(e: FormEvent) {
    e.preventDefault()
    const result = await act.run(() => proposeBank(vendorId, { ...f, swiftBic: f.swiftBic || undefined, iban: f.iban || undefined }))
    if (result) {
      setF({ ...f, iban: '', reason: '' })
      onDone()
    }
  }

  return (
    <form onSubmit={submit} className="stack">
      <h3>{t('bank.propose')}</h3>
      <label>{t('bank.accountName')}<input value={f.accountName} onChange={set('accountName')} required /></label>
      <label>{t('bank.bankName')}<input value={f.bankName} onChange={set('bankName')} required /></label>
      <label>{t('bank.swift')}<input value={f.swiftBic} onChange={set('swiftBic')} dir="ltr" /></label>
      <label>{t('bank.country')}<input value={f.countryCode} onChange={set('countryCode')} required maxLength={2} dir="ltr" /></label>
      <label>{t('bank.currency')}<input value={f.currencyCode} onChange={set('currencyCode')} required maxLength={3} dir="ltr" /></label>
      <label>{t('bank.iban')}<input value={f.iban} onChange={set('iban')} dir="ltr" autoComplete="off" /></label>
      <label>{t('bank.reason')}<input value={f.reason} onChange={set('reason')} required maxLength={500} /></label>
      <ProblemAlert error={act.error} />
      <button type="submit" disabled={act.busy}>{t('bank.submitChange')}</button>
    </form>
  )
}

function InviteForm({ vendorId }: { vendorId: string }) {
  const { t } = useTranslation()
  const act = useAction()
  const [f, setF] = useState({ email: '', displayName: '' })
  const [url, setUrl] = useState<string>()

  async function submit(e: FormEvent) {
    e.preventDefault()
    const r = await act.run(() => inviteVendorUser(vendorId, f.email, f.displayName))
    if (r) {
      setUrl(r.inviteUrl)
      setF({ email: '', displayName: '' })
    }
  }

  return (
    <form onSubmit={submit} className="stack">
      <label>{t('portalAdmin.email')}<input type="email" value={f.email} onChange={(e) => setF({ ...f, email: e.target.value })} required dir="ltr" /></label>
      <label>{t('portalAdmin.name')}<input value={f.displayName} onChange={(e) => setF({ ...f, displayName: e.target.value })} required /></label>
      <ProblemAlert error={act.error} />
      <button type="submit" disabled={act.busy}>{t('portalAdmin.invite')}</button>
      {url && (
        <p role="status">
          {t('portalAdmin.inviteLink')} <code dir="ltr">{url}</code>
        </p>
      )}
    </form>
  )
}

function NewRequestForm({ vendorId, onDone }: { vendorId: string; onDone: () => void }) {
  const { t } = useTranslation()
  const act = useAction()
  const [f, setF] = useState({ subject: '', body: '' })

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (await act.run(() => createVendorRequest(vendorId, f.subject, f.body))) {
      setF({ subject: '', body: '' })
      onDone()
    }
  }

  return (
    <form onSubmit={submit} className="stack">
      <label>{t('vendors.subject')}<input value={f.subject} onChange={(e) => setF({ ...f, subject: e.target.value })} required maxLength={200} /></label>
      <label>{t('vendors.message')}<input value={f.body} onChange={(e) => setF({ ...f, body: e.target.value })} required maxLength={4000} /></label>
      <ProblemAlert error={act.error} />
      <button type="submit" disabled={act.busy}>{t('vendors.sendRequest')}</button>
    </form>
  )
}
