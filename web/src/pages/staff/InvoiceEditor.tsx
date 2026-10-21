import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import type { InvoiceData, InvoiceLineInput } from '../../api/invoices'
import type { LegalEntityDto, VendorListItem } from '../../api/staff'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'

const num = (v: string) => (v === '' ? 0 : Number(v))
import { blank } from './invoiceDefaults'

interface Props {
  initial: InvoiceData
  entities?: LegalEntityDto[]
  vendors?: VendorListItem[]
  fixedTarget?: boolean
  onSave: (data: InvoiceData, entityId: string, vendorId: string) => Promise<unknown>
}

/** Header and lines of an invoice. Amounts are typed by a person; the system checks them, it never changes them. */
export function InvoiceEditor({ initial, entities = [], vendors = [], fixedTarget = false, onSave }: Props) {
  const { t } = useTranslation()
  const act = useAction()
  const [d, setD] = useState<InvoiceData>(initial)
  const [entityId, setEntityId] = useState(entities[0]?.id ?? '')
  const [vendorId, setVendorId] = useState(vendors[0]?.id ?? '')
  const set = <K extends keyof InvoiceData>(k: K, v: InvoiceData[K]) => setD({ ...d, [k]: v })
  const setLine = (i: number, patch: Partial<InvoiceLineInput>) => set('lines', d.lines.map((l, n) => (n === i ? { ...l, ...patch } : l)))

  async function submit(e: FormEvent) {
    e.preventDefault()
    await act.run(() => onSave({ ...d, poReference: d.poReference || null, supplierTaxInvoiceRef: d.supplierTaxInvoiceRef || null, supplierTaxNumber: d.supplierTaxNumber || null,
      dueDate: d.dueDate || null }, entityId, vendorId))
  }

  return (
    <form onSubmit={submit} className="stack">
      {!fixedTarget && (
        <div className="row">
          <label>
            {t('invoices.entity')}
            <select value={entityId} onChange={(e) => setEntityId(e.target.value)} required>
              {entities.map((x) => (
                <option key={x.id} value={x.id}>{x.code} — {x.legalName}</option>
              ))}
            </select>
          </label>
          <label>
            {t('invoices.vendor')}
            <select value={vendorId} onChange={(e) => setVendorId(e.target.value)} required>
              {vendors.map((x) => (
                <option key={x.id} value={x.id}>{x.legalName}</option>
              ))}
            </select>
          </label>
        </div>
      )}
      <div className="row">
        <label>{t('invoices.number')}<input value={d.invoiceNumber} onChange={(e) => set('invoiceNumber', e.target.value)} required maxLength={100} dir="ltr" /></label>
        <label>{t('invoices.date')}<input type="date" value={d.invoiceDate} onChange={(e) => set('invoiceDate', e.target.value)} required dir="ltr" /></label>
        <label>{t('invoices.dueDate')}<input type="date" value={d.dueDate ?? ''} onChange={(e) => set('dueDate', e.target.value || null)} dir="ltr" /></label>
        <label>{t('invoices.currency')}<input value={d.currencyCode} onChange={(e) => set('currencyCode', e.target.value.toUpperCase())} required maxLength={3} dir="ltr" /></label>
      </div>
      <div className="row">
        <label>{t('invoices.subtotal')}<input type="number" step="any" min="0" value={d.subtotal} onChange={(e) => set('subtotal', num(e.target.value))} dir="ltr" /></label>
        <label>{t('invoices.taxTotal')}<input type="number" step="any" min="0" value={d.taxTotal} onChange={(e) => set('taxTotal', num(e.target.value))} dir="ltr" /></label>
        <label>{t('invoices.total')}<input type="number" step="any" min="0" value={d.total} onChange={(e) => set('total', num(e.target.value))} dir="ltr" /></label>
      </div>
      <div className="row">
        <label>{t('invoices.poReference')}<input value={d.poReference ?? ''} onChange={(e) => set('poReference', e.target.value)} maxLength={100} dir="ltr" /></label>
        <label>{t('invoices.supplierTaxNumber')}<input value={d.supplierTaxNumber ?? ''} onChange={(e) => set('supplierTaxNumber', e.target.value)} maxLength={30} dir="ltr" /></label>
        <label>{t('invoices.supplierTaxInvoiceRef')}<input value={d.supplierTaxInvoiceRef ?? ''} onChange={(e) => set('supplierTaxInvoiceRef', e.target.value)} maxLength={200} dir="ltr" /></label>
      </div>

      <h3>{t('invoices.lines')}</h3>
      <div className="table-wrap" tabIndex={0}>
        <table>
          <thead>
            <tr>
              <th scope="col">{t('invoices.lineDescription')}</th>
              <th scope="col">{t('invoices.quantity')}</th>
              <th scope="col">{t('invoices.unitPrice')}</th>
              <th scope="col">{t('invoices.lineAmount')}</th>
              <th scope="col">{t('invoices.taxCode')}</th>
              <th scope="col">{t('invoices.taxRate')}</th>
              <th scope="col">{t('invoices.taxAmount')}</th>
              <th scope="col">{t('invoices.poLine')}</th>
              <th scope="col">{t('invoices.removeLine')}</th>
            </tr>
          </thead>
          <tbody>
            {d.lines.map((l, i) => (
              <tr key={i}>
                <td><input aria-label={t('invoices.lineDescription')} value={l.description} onChange={(e) => setLine(i, { description: e.target.value })} required maxLength={500} /></td>
                <td><input aria-label={t('invoices.quantity')} type="number" step="any" min="0" value={l.quantity} onChange={(e) => setLine(i, { quantity: num(e.target.value) })} dir="ltr" /></td>
                <td><input aria-label={t('invoices.unitPrice')} type="number" step="any" min="0" value={l.unitPrice} onChange={(e) => setLine(i, { unitPrice: num(e.target.value) })} dir="ltr" /></td>
                <td><input aria-label={t('invoices.lineAmount')} type="number" step="any" min="0" value={l.lineAmount} onChange={(e) => setLine(i, { lineAmount: num(e.target.value) })} dir="ltr" /></td>
                <td><input aria-label={t('invoices.taxCode')} value={l.taxCode ?? ''} onChange={(e) => setLine(i, { taxCode: e.target.value || null })} maxLength={30} dir="ltr" /></td>
                <td><input aria-label={t('invoices.taxRate')} type="number" step="any" min="0" max="1" value={l.taxRate} onChange={(e) => setLine(i, { taxRate: num(e.target.value) })} dir="ltr" /></td>
                <td><input aria-label={t('invoices.taxAmount')} type="number" step="any" min="0" value={l.taxAmount} onChange={(e) => setLine(i, { taxAmount: num(e.target.value) })} dir="ltr" /></td>
                <td><input aria-label={t('invoices.poLine')} value={l.poLineRef ?? ''} onChange={(e) => setLine(i, { poLineRef: e.target.value || null })} maxLength={50} dir="ltr" /></td>
                <td><button type="button" className="secondary" onClick={() => set('lines', d.lines.filter((_, n) => n !== i))}>{t('invoices.removeLine')}</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="row">
        <button type="button" className="secondary" onClick={() => set('lines', [...d.lines, blank()])}>{t('invoices.addLine')}</button>
      </div>
      <ProblemAlert error={act.error} />
      <button type="submit" disabled={act.busy}>{t('invoices.save')}</button>
    </form>
  )
}
