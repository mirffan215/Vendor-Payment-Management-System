import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import {
  getPortalInvoices, submitPortalInvoice, cancelBankChange, getBankChanges, getBankDetails, getDocuments, getEntities, getProfile, getRequests, proposeBank,
  replyToRequest, updateProfile, uploadDocument, type PortalSessionInfo,
} from '../../api/portal'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDate, formatMoney, type CalendarPreference } from '../../i18n/format'

const TABS = ['profile', 'documents', 'invoices', 'bank', 'messages'] as const
type Tab = (typeof TABS)[number]
const DOC_TYPES = ['CommercialRegistration', 'TaxCertificate', 'BankLetter', 'Other']

export function PortalHome({ session, calendar }: { session: PortalSessionInfo; calendar: CalendarPreference }) {
  const { t } = useTranslation()
  const [tab, setTab] = useState<Tab>('profile')
  return (
    <section className="card" aria-labelledby="phome-title">
      <h1 id="phome-title">{t('portal.welcome', { vendor: session.vendorName ?? '', name: session.displayName })}</h1>
      <div role="tablist" aria-label={t('portal.sections')} className="row">
        {TABS.map((id) => (
          <button key={id} role="tab" type="button" aria-selected={tab === id} className={tab === id ? 'active' : 'secondary'} onClick={() => setTab(id)}>
            {t(`portal.tabs.${id}`)}
          </button>
        ))}
      </div>
      <div role="tabpanel">
        {tab === 'profile' && <Profile />}
        {tab === 'documents' && <Documents calendar={calendar} />}
        {tab === 'invoices' && <Invoices calendar={calendar} />}
        {tab === 'bank' && <Bank calendar={calendar} />}
        {tab === 'messages' && <Messages />}
      </div>
    </section>
  )
}

function Profile() {
  const { t } = useTranslation()
  const profile = useLoad(getProfile, 'profile')
  const entities = useLoad(getEntities, 'entities')
  const act = useAction()
  const [f, setF] = useState<{ legalNameAr: string; tradeName: string; contactEmail: string; contactPhone: string }>()
  const [saved, setSaved] = useState(false)

  const p = profile.data
  const form = f ?? (p ? { legalNameAr: p.legalNameAr ?? '', tradeName: p.tradeName ?? '', contactEmail: p.contactEmail ?? '', contactPhone: p.contactPhone ?? '' } : undefined)
  if (!p || !form) return profile.error ? <ProblemAlert error={profile.error} /> : <p role="status">{t('common.loading')}</p>

  async function save(e: FormEvent) {
    e.preventDefault()
    if (await act.run(() => updateProfile(form!))) setSaved(true)
  }

  return (
    <div className="stack">
      <dl>
        <dt>{t('vendors.legalName')}</dt>
        <dd>{p.legalName}</dd>
        <dt>{t('vendors.status')}</dt>
        <dd>{t(`status.${p.status}`, { defaultValue: p.status })}</dd>
      </dl>
      <p className="muted">{t('portal.legalNameLocked')}</p>
      <form onSubmit={save} className="stack">
        <label>{t('portal.legalNameAr')}<input value={form.legalNameAr} onChange={(e) => { setSaved(false); setF({ ...form, legalNameAr: e.target.value }) }} dir="rtl" /></label>
        <label>{t('portal.tradeName')}<input value={form.tradeName} onChange={(e) => { setSaved(false); setF({ ...form, tradeName: e.target.value }) }} /></label>
        <label>{t('portal.contactEmail')}<input type="email" value={form.contactEmail} onChange={(e) => { setSaved(false); setF({ ...form, contactEmail: e.target.value }) }} dir="ltr" /></label>
        <label>{t('portal.contactPhone')}<input value={form.contactPhone} onChange={(e) => { setSaved(false); setF({ ...form, contactPhone: e.target.value }) }} dir="ltr" /></label>
        <ProblemAlert error={act.error} />
        {saved && <p role="status">{t('portal.saved')}</p>}
        <button type="submit" disabled={act.busy}>{t('portal.save')}</button>
      </form>
      <h2>{t('portal.entities')}</h2>
      <ul className="plain">
        {(entities.data ?? []).map((l) => (
          <li key={l.legalEntityId}>
            {l.legalEntityName ?? l.legalEntityId} · <span dir="ltr">{l.defaultCurrency}</span> · {t(`status.${l.status}`, { defaultValue: l.status })}
          </li>
        ))}
      </ul>
    </div>
  )
}

function Documents({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const docs = useLoad(getDocuments, 'docs')
  const act = useAction()
  const [type, setType] = useState(DOC_TYPES[0])
  const [file, setFile] = useState<File>()

  async function upload(e: FormEvent) {
    e.preventDefault()
    if (!file) return
    if (await act.run(() => uploadDocument(type, file))) {
      setFile(undefined)
      docs.reload()
    }
  }

  return (
    <div className="stack">
      <ProblemAlert error={docs.error} />
      <ul className="plain">
        {(docs.data ?? []).map((d) => (
          <li key={d.id} className="row">
            <span>{t(`documentType.${d.type}`, { defaultValue: d.type })}</span>
            <span dir="ltr">{d.fileName}</span>
            <span>{t(`scan.${d.scanStatus}`, { defaultValue: d.scanStatus })}</span>
            <span>{t(`docStatus.${d.status}`, { defaultValue: d.status })}</span>
            <span>{formatDate(d.createdAt, i18n.language, calendar)}</span>
            {d.reviewNote && <span>{d.reviewNote}</span>}
            {d.scanStatus === 'Clean' && <a href={`/api/v1/portal/documents/${d.id}/file`}>{t('portal.download')}</a>}
          </li>
        ))}
      </ul>
      <form onSubmit={upload} className="stack">
        <h2>{t('portal.uploadTitle')}</h2>
        <label>
          {t('portal.documentType')}
          <select value={type} onChange={(e) => setType(e.target.value)}>
            {DOC_TYPES.map((x) => (
              <option key={x} value={x}>{t(`documentType.${x}`)}</option>
            ))}
          </select>
        </label>
        <label>
          {t('portal.file')}
          <input type="file" onChange={(e) => setFile(e.target.files?.[0])} accept=".pdf,.png,.jpg,.jpeg" />
        </label>
        <p className="muted">{t('portal.scanNote')}</p>
        <ProblemAlert error={act.error} />
        <button type="submit" disabled={act.busy || !file}>{t('portal.upload')}</button>
      </form>
    </div>
  )
}

function Bank({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const details = useLoad(getBankDetails, 'bank')
  const changes = useLoad(getBankChanges, 'changes')
  const act = useAction()
  const [f, setF] = useState({ accountName: '', bankName: '', swiftBic: '', countryCode: 'SA', currencyCode: 'SAR', iban: '', reason: '' })
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value })
  const reload = () => {
    details.reload()
    changes.reload()
  }

  async function submit(e: FormEvent) {
    e.preventDefault()
    const body: Record<string, string> = { ...f }
    if (!f.swiftBic) delete body.swiftBic
    if (await act.run(() => proposeBank(body))) {
      setF({ ...f, iban: '', reason: '' })
      reload()
    }
  }

  return (
    <div className="stack">
      <p>{t('portal.bankNote')}</p>
      <h2>{t('portal.currentBank')}</h2>
      <ProblemAlert error={details.error} />
      <ul className="plain">
        {(details.data ?? []).filter((b) => b.status === 'Verified').map((b) => (
          <li key={b.id}>{b.bankName} · <span dir="ltr">{b.maskedAccount}</span> · <span dir="ltr">{b.currencyCode}</span></li>
        ))}
      </ul>
      <h2>{t('portal.bankRequests')}</h2>
      <ul className="plain">
        {(changes.data ?? []).map((c) => (
          <li key={c.id} className="row">
            <span>{c.proposed.bankName} · <span dir="ltr">{c.proposed.maskedAccount}</span></span>
            <span>{t(`changeStatus.${c.status}`, { defaultValue: c.status })}</span>
            <span>{formatDate(c.madeAt, i18n.language, calendar)}</span>
            {c.status === 'Pending' && (
              <button type="button" disabled={act.busy} onClick={() => void act.run(() => cancelBankChange(c.id)).then(reload)}>{t('portal.withdraw')}</button>
            )}
          </li>
        ))}
      </ul>
      <form onSubmit={submit} className="stack">
        <h2>{t('portal.requestBankChange')}</h2>
        <label>{t('bank.accountName')}<input value={f.accountName} onChange={set('accountName')} required /></label>
        <label>{t('bank.bankName')}<input value={f.bankName} onChange={set('bankName')} required /></label>
        <label>{t('bank.swift')}<input value={f.swiftBic} onChange={set('swiftBic')} dir="ltr" /></label>
        <label>{t('bank.country')}<input value={f.countryCode} onChange={set('countryCode')} required maxLength={2} dir="ltr" /></label>
        <label>{t('bank.currency')}<input value={f.currencyCode} onChange={set('currencyCode')} required maxLength={3} dir="ltr" /></label>
        <label>{t('bank.iban')}<input value={f.iban} onChange={set('iban')} required dir="ltr" autoComplete="off" /></label>
        <label>{t('bank.reason')}<input value={f.reason} onChange={set('reason')} required maxLength={500} /></label>
        <ProblemAlert error={act.error} />
        <button type="submit" disabled={act.busy}>{t('portal.sendBankRequest')}</button>
      </form>
    </div>
  )
}

function Messages() {
  const { t } = useTranslation()
  const list = useLoad(getRequests, 'requests')
  const act = useAction()
  const [reply, setReply] = useState<Record<string, string>>({})
  return (
    <div className="stack">
      <ProblemAlert error={list.error} />
      {list.data?.length === 0 && <p>{t('portal.noMessages')}</p>}
      <ul className="plain">
        {(list.data ?? []).map((r) => (
          <li key={r.id} className="card inner">
            <h2>{r.subject}</h2>
            <ul className="plain">
              {r.messages.map((m) => (
                <li key={m.id}>
                  <strong>{t(`direction.${m.direction}`, { defaultValue: m.direction })}:</strong> {m.body}
                </li>
              ))}
            </ul>
            {r.status !== 'Closed' && (
              <form
                className="stack"
                onSubmit={(e) => {
                  e.preventDefault()
                  void act.run(() => replyToRequest(r.id, reply[r.id] ?? '')).then((x) => {
                    if (x) {
                      setReply({ ...reply, [r.id]: '' })
                      list.reload()
                    }
                  })
                }}
              >
                <label>{t('portal.reply')}<input value={reply[r.id] ?? ''} onChange={(e) => setReply({ ...reply, [r.id]: e.target.value })} required maxLength={4000} /></label>
                <button type="submit" disabled={act.busy}>{t('portal.sendReply')}</button>
              </form>
            )}
          </li>
        ))}
      </ul>
      <ProblemAlert error={act.error} />
    </div>
  )
}

function Invoices({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const list = useLoad(getPortalInvoices, 'invoices')
  const entities = useLoad(getEntities, 'entities')
  const act = useAction()
  const [today] = useState(() => new Date().toISOString().slice(0, 10))
  const [f, setF] = useState({ legalEntityId: '', invoiceNumber: '', invoiceDate: today, dueDate: '', currencyCode: 'SAR', subtotal: '', taxTotal: '', total: '', poReference: '', supplierTaxNumber: '' })
  const [file, setFile] = useState<File>()
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value })
  const entityId = f.legalEntityId || entities.data?.[0]?.legalEntityId || ''

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!file) return
    if (await act.run(() => submitPortalInvoice({ ...f, legalEntityId: entityId }, file))) {
      setF({ ...f, invoiceNumber: '', subtotal: '', taxTotal: '', total: '', poReference: '' })
      setFile(undefined)
      list.reload()
    }
  }

  return (
    <div className="stack">
      <ProblemAlert error={list.error} />
      {list.data?.length === 0 && <p>{t('portal.noInvoices')}</p>}
      <ul className="plain">
        {(list.data ?? []).map((i) => (
          <li key={i.id} className="row">
            <span dir="ltr">{i.invoiceNumber}</span>
            <span>{i.entityName}</span>
            <span>{formatDate(i.invoiceDate, i18n.language, calendar)}</span>
            <span dir="ltr">{formatMoney(i.total, i.currencyCode, i18n.language)}</span>
            <span>{t(`portalInvoiceStatus.${i.status}`, { defaultValue: i.status })}</span>
          </li>
        ))}
      </ul>
      <form onSubmit={submit} className="stack">
        <h2>{t('portal.submitInvoice')}</h2>
        <p className="muted">{t('portal.submitInvoiceHelp')}</p>
        <label>
          {t('portal.customerEntity')}
          <select value={entityId} onChange={set('legalEntityId')} required>
            {(entities.data ?? []).map((x) => (<option key={x.legalEntityId} value={x.legalEntityId}>{x.legalEntityName ?? x.legalEntityId}</option>))}
          </select>
        </label>
        <div className="row">
          <label>{t('invoices.number')}<input value={f.invoiceNumber} onChange={set('invoiceNumber')} required maxLength={100} dir="ltr" /></label>
          <label>{t('invoices.date')}<input type="date" value={f.invoiceDate} onChange={set('invoiceDate')} required dir="ltr" /></label>
          <label>{t('invoices.dueDate')}<input type="date" value={f.dueDate} onChange={set('dueDate')} dir="ltr" /></label>
          <label>{t('invoices.currency')}<input value={f.currencyCode} onChange={(e) => setF({ ...f, currencyCode: e.target.value.toUpperCase() })} required maxLength={3} dir="ltr" /></label>
        </div>
        <div className="row">
          <label>{t('invoices.subtotal')}<input type="number" step="any" min="0" value={f.subtotal} onChange={set('subtotal')} required dir="ltr" /></label>
          <label>{t('invoices.taxTotal')}<input type="number" step="any" min="0" value={f.taxTotal} onChange={set('taxTotal')} required dir="ltr" /></label>
          <label>{t('invoices.total')}<input type="number" step="any" min="0" value={f.total} onChange={set('total')} required dir="ltr" /></label>
        </div>
        <div className="row">
          <label>{t('invoices.poReference')}<input value={f.poReference} onChange={set('poReference')} maxLength={100} dir="ltr" /></label>
          <label>{t('invoices.supplierTaxNumber')}<input value={f.supplierTaxNumber} onChange={set('supplierTaxNumber')} maxLength={30} dir="ltr" /></label>
          <label>{t('portal.file')}<input type="file" accept=".pdf,.png,.jpg,.jpeg" onChange={(e) => setFile(e.target.files?.[0])} /></label>
        </div>
        <ProblemAlert error={act.error} />
        <button type="submit" disabled={act.busy || !file}>{t('portal.submitInvoiceButton')}</button>
      </form>
    </div>
  )
}
