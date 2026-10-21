import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import {
  assignIntakeMessage, dismissIntakeMessage, getMailbox, importPurchaseOrders, listIntakeMessages, listPurchaseOrders, saveMailbox, type IntakeMessage, type PoImportResult,
} from '../../api/invoices'
import { listEntities, listVendors } from '../../api/staff'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDateTime, type CalendarPreference } from '../../i18n/format'

const TABS = ['mail', 'mailbox', 'orders'] as const
type Tab = (typeof TABS)[number]

export function IntakePage({ calendar }: { calendar: CalendarPreference }) {
  const { t } = useTranslation()
  const [tab, setTab] = useState<Tab>('mail')
  return (
    <section className="card" aria-labelledby="intake-title">
      <h1 id="intake-title">{t('intake.title')}</h1>
      <div role="tablist" aria-label={t('intake.title')} className="row">
        {TABS.map((id) => (
          <button key={id} role="tab" type="button" aria-selected={tab === id} className={tab === id ? 'active' : 'secondary'} onClick={() => setTab(id)}>{t(`intake.tabs.${id}`)}</button>
        ))}
      </div>
      <div role="tabpanel">
        {tab === 'mail' && <Mail calendar={calendar} />}
        {tab === 'mailbox' && <MailboxSettings />}
        {tab === 'orders' && <Orders />}
      </div>
    </section>
  )
}

function Mail({ calendar }: { calendar: CalendarPreference }) {
  const { t } = useTranslation()
  const messages = useLoad(() => listIntakeMessages('Unmatched'), 'unmatched')
  const vendors = useLoad(() => listVendors('Active', ''), 'vendors')
  return (
    <div className="stack">
      <p>{t('intake.unmatchedIntro')}</p>
      <ProblemAlert error={messages.error} />
      {messages.data?.length === 0 && <p>{t('intake.noUnmatched')}</p>}
      <ul className="plain">
        {(messages.data ?? []).map((m) => (<MessageCard key={m.id} message={m} vendors={vendors.data ?? []} calendar={calendar} onDone={messages.reload} />))}
      </ul>
    </div>
  )
}

function MessageCard({ message: m, vendors, calendar, onDone }: {
  message: IntakeMessage
  vendors: { id: string; legalName: string }[]
  calendar: CalendarPreference
  onDone: () => void
}) {
  const { t, i18n } = useTranslation()
  const act = useAction()
  const [vendorId, setVendorId] = useState('')
  const [reason, setReason] = useState('')
  return (
    <li className="card inner">
      <h2 dir="ltr">{m.fromAddress}</h2>
      <p>{m.subject} · {formatDateTime(m.receivedAt, i18n.language, calendar)} · {t('intake.attachments', { count: m.attachmentCount })}</p>
      {m.note && <p className="muted">{m.note}</p>}
      <div className="row">
        <label>
          {t('invoices.vendor')}
          <select value={vendorId} onChange={(e) => setVendorId(e.target.value)}>
            <option value="">{t('intake.chooseVendor')}</option>
            {vendors.map((v) => (<option key={v.id} value={v.id}>{v.legalName}</option>))}
          </select>
        </label>
        <button type="button" disabled={act.busy || !vendorId} onClick={() => void act.run(() => assignIntakeMessage(m.id, vendorId)).then((r) => r && onDone())}>{t('intake.assign')}</button>
        <label>{t('vendors.reason')}<input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} /></label>
        <button type="button" className="secondary" disabled={act.busy || !reason.trim()} onClick={() => void act.run(() => dismissIntakeMessage(m.id, reason)).then((r) => r && onDone())}>{t('intake.dismiss')}</button>
      </div>
      <ProblemAlert error={act.error} />
    </li>
  )
}

function MailboxSettings() {
  const { t } = useTranslation()
  const entities = useLoad(listEntities, 'entities')
  const [entityId, setEntityId] = useState('')
  const current = entityId || entities.data?.[0]?.id || ''
  const mailbox = useLoad(() => (current ? getMailbox(current) : Promise.resolve(null)), current)
  const act = useAction()
  const [address, setAddress] = useState<string>()
  const [enabled, setEnabled] = useState<boolean>()
  const shownAddress = address ?? mailbox.data?.address ?? ''
  const shownEnabled = enabled ?? mailbox.data?.enabled ?? true

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (await act.run(() => saveMailbox(current, shownAddress, shownEnabled))) {
      setAddress(undefined)
      setEnabled(undefined)
      mailbox.reload()
    }
  }

  return (
    <form onSubmit={submit} className="stack">
      <p>{t('intake.mailboxIntro')}</p>
      <label>
        {t('invoices.entity')}
        <select value={current} onChange={(e) => { setEntityId(e.target.value); setAddress(undefined); setEnabled(undefined) }}>
          {(entities.data ?? []).map((x) => (<option key={x.id} value={x.id}>{x.code} — {x.legalName}</option>))}
        </select>
      </label>
      <label>{t('intake.mailboxAddress')}<input type="email" value={shownAddress} onChange={(e) => setAddress(e.target.value)} required dir="ltr" /></label>
      <label><input type="checkbox" checked={shownEnabled} onChange={(e) => setEnabled(e.target.checked)} />{t('verificationAdmin.enabled')}</label>
      {mailbox.data?.lastError && <p role="alert" className="alert">{t('intake.lastError')}</p>}
      <ProblemAlert error={act.error ?? mailbox.error} />
      <button type="submit" disabled={act.busy}>{t('invoices.save')}</button>
    </form>
  )
}

const SAMPLE = JSON.stringify([{ poNumber: 'PO-1001', version: 1, erpVendorAccount: 'V-1001', currencyCode: 'SAR', status: 'Open', orderDate: '2026-01-15', previouslyInvoicedValue: 0,
  lines: [{ lineNo: '1', description: 'Consulting', orderedQuantity: 10, unitPrice: 100, previouslyInvoicedQuantity: 0 }] }], null, 2)

function Orders() {
  const { t } = useTranslation()
  const entities = useLoad(listEntities, 'entities')
  const [entityId, setEntityId] = useState('')
  const current = entityId || entities.data?.[0]?.id || ''
  const orders = useLoad(() => (current ? listPurchaseOrders(current, '') : Promise.resolve([])), current)
  const act = useAction()
  const [text, setText] = useState(SAMPLE)
  const [result, setResult] = useState<PoImportResult>()
  const [parseError, setParseError] = useState(false)

  async function submit(e: FormEvent) {
    e.preventDefault()
    let items: unknown
    try {
      items = JSON.parse(text)
      setParseError(false)
    } catch {
      setParseError(true)
      return
    }
    const r = await act.run(() => importPurchaseOrders(current, Array.isArray(items) ? items : []))
    if (r) {
      setResult(r)
      orders.reload()
    }
  }

  return (
    <div className="stack">
      <p>{t('intake.ordersIntro')}</p>
      <label>
        {t('invoices.entity')}
        <select value={current} onChange={(e) => setEntityId(e.target.value)}>
          {(entities.data ?? []).map((x) => (<option key={x.id} value={x.id}>{x.code} — {x.legalName}</option>))}
        </select>
      </label>
      <ul className="plain">
        {(orders.data ?? []).map((o) => (
          <li key={o.id}><span dir="ltr">{o.poNumber}</span> · {t('invoices.versionShort', { version: o.version })} · {t(`poStatus.${o.status}`, { defaultValue: o.status })} · <span dir="ltr">{o.currencyCode}</span> · {t('intake.poLines', { count: o.lines.length })}</li>
        ))}
      </ul>
      <form onSubmit={submit} className="stack">
        <label>
          {t('intake.importJson')}
          <textarea value={text} onChange={(e) => setText(e.target.value)} rows={10} dir="ltr" spellCheck={false} />
        </label>
        {parseError && <p role="alert" className="alert">{t('intake.invalidJson')}</p>}
        <ProblemAlert error={act.error} />
        <button type="submit" disabled={act.busy}>{t('intake.import')}</button>
      </form>
      {result && (
        <div role="status">
          <p>{t('intake.importResult', { created: result.created, updated: result.updated, unchanged: result.unchanged })}</p>
          <ul className="plain">
            {result.errors.map((er, n) => (<li key={n}><span dir="ltr">{er.poNumber}</span>: {t(`errors.codes.${er.code}`, { defaultValue: er.code })}</li>))}
          </ul>
        </div>
      )}
    </div>
  )
}
