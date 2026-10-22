import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate } from 'react-router-dom'
import { createFromDocument, createInvoice, listInvoices } from '../../api/invoices'
import { listEntities, listVendors } from '../../api/staff'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDate, formatMoney, type CalendarPreference } from '../../i18n/format'
import { InvoiceEditor } from './InvoiceEditor'
import { emptyInvoice } from './invoiceDefaults'

const INVOICE_STATUSES = ['Draft', 'Received', 'InVerification', 'Exception', 'Verified', 'ReadyForApproval', 'Rejected', 'Cancelled']
const SOURCES = ['Manual', 'Portal', 'Email', 'Document']

export function InvoicesPage({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const navigate = useNavigate()
  const [filters, setFilters] = useState({ status: '', search: '', legalEntityId: '', source: '' })
  const [applied, setApplied] = useState(filters)
  const list = useLoad(() => listInvoices(applied), JSON.stringify(applied))
  const entities = useLoad(listEntities, 'entities')
  const vendors = useLoad(() => listVendors('Active', ''), 'vendors-active')
  const [mode, setMode] = useState<'none' | 'new' | 'document'>('none')

  return (
    <section className="card" aria-labelledby="inv-title">
      <h1 id="inv-title">{t('invoices.title')}</h1>
      <form className="row" onSubmit={(e) => { e.preventDefault(); setApplied(filters) }}>
        <label>{t('invoices.search')}<input value={filters.search} onChange={(e) => setFilters({ ...filters, search: e.target.value })} /></label>
        <label>
          {t('vendors.status')}
          <select value={filters.status} onChange={(e) => setFilters({ ...filters, status: e.target.value })}>
            <option value="">{t('vendors.allStatuses')}</option>
            {INVOICE_STATUSES.map((s) => (<option key={s} value={s}>{t(`invoiceStatus.${s}`)}</option>))}
          </select>
        </label>
        <label>
          {t('invoices.entity')}
          <select value={filters.legalEntityId} onChange={(e) => setFilters({ ...filters, legalEntityId: e.target.value })}>
            <option value="">{t('invoices.allEntities')}</option>
            {(entities.data ?? []).map((x) => (<option key={x.id} value={x.id}>{x.code}</option>))}
          </select>
        </label>
        <label>
          {t('invoices.source')}
          <select value={filters.source} onChange={(e) => setFilters({ ...filters, source: e.target.value })}>
            <option value="">{t('invoices.allSources')}</option>
            {SOURCES.map((s) => (<option key={s} value={s}>{t(`invoiceSource.${s}`)}</option>))}
          </select>
        </label>
        <button type="submit">{t('vendors.filter')}</button>
      </form>

      <ProblemAlert error={list.error} />
      {list.loading && !list.data && <p role="status">{t('common.loading')}</p>}
      {list.data?.length === 0 && <p>{t('invoices.empty')}</p>}
      {list.data && list.data.length > 0 && (
        <div className="table-wrap" tabIndex={0}>
          <table>
            <thead>
              <tr>
                <th scope="col">{t('invoices.number')}</th>
                <th scope="col">{t('invoices.vendor')}</th>
                <th scope="col">{t('invoices.date')}</th>
                <th scope="col">{t('invoices.total')}</th>
                <th scope="col">{t('invoices.source')}</th>
                <th scope="col">{t('vendors.status')}</th>
              </tr>
            </thead>
            <tbody>
              {list.data.map((i) => (
                <tr key={i.id}>
                  <td dir="ltr"><Link to={`/invoices/${i.id}`}>{i.invoiceNumber || t('invoices.noNumber')}</Link></td>
                  <td>{i.vendorName}</td>
                  <td>{formatDate(i.invoiceDate, i18n.language, calendar)}</td>
                  <td>{formatMoney(i.total, i.currencyCode, i18n.language)}</td>
                  <td>{t(`invoiceSource.${i.source}`, { defaultValue: i.source })}</td>
                  <td>{t(`invoiceStatus.${i.status}`, { defaultValue: i.status })}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="row">
        <button type="button" className={mode === 'new' ? 'active' : 'secondary'} onClick={() => setMode(mode === 'new' ? 'none' : 'new')}>{t('invoices.newInvoice')}</button>
        <button type="button" className={mode === 'document' ? 'active' : 'secondary'} onClick={() => setMode(mode === 'document' ? 'none' : 'document')}>{t('invoices.fromDocument')}</button>
      </div>
      {mode === 'new' && entities.data && vendors.data && (
        <InvoiceEditor
          initial={emptyInvoice()}
          entities={entities.data}
          vendors={vendors.data}
          onSave={async (data, entityId, vendorId) => navigate(`/invoices/${(await createInvoice(entityId, vendorId, data)).id}`)}
        />
      )}
      {mode === 'document' && entities.data && vendors.data && (
        <DocumentForm entities={entities.data} vendors={vendors.data} onCreated={(id) => navigate(`/invoices/${id}`)} />
      )}
    </section>
  )
}

function DocumentForm({ entities, vendors, onCreated }: {
  entities: { id: string; code: string; legalName: string }[]
  vendors: { id: string; legalName: string }[]
  onCreated: (id: string) => void
}) {
  const { t } = useTranslation()
  const act = useAction()
  const [entityId, setEntityId] = useState(entities[0]?.id ?? '')
  const [vendorId, setVendorId] = useState(vendors[0]?.id ?? '')
  const [file, setFile] = useState<File>()

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!file) return
    const created = await act.run(() => createFromDocument(entityId, vendorId, file))
    if (created) onCreated(created.id)
  }

  return (
    <form onSubmit={submit} className="stack">
      <p>{t('invoices.fromDocumentHelp')}</p>
      <div className="row">
        <label>
          {t('invoices.entity')}
          <select value={entityId} onChange={(e) => setEntityId(e.target.value)}>
            {entities.map((x) => (<option key={x.id} value={x.id}>{x.code} — {x.legalName}</option>))}
          </select>
        </label>
        <label>
          {t('invoices.vendor')}
          <select value={vendorId} onChange={(e) => setVendorId(e.target.value)}>
            {vendors.map((x) => (<option key={x.id} value={x.id}>{x.legalName}</option>))}
          </select>
        </label>
        <label>{t('portal.file')}<input type="file" accept=".pdf,.png,.jpg,.jpeg" onChange={(e) => setFile(e.target.files?.[0])} /></label>
      </div>
      <ProblemAlert error={act.error} />
      <button type="submit" disabled={act.busy || !file}>{t('invoices.createDraft')}</button>
    </form>
  )
}
