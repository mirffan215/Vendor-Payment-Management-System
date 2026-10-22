import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router-dom'
import {
  cancelInvoice, dismissExtraction, getExtractions, getInvoice, getVerification, invoiceAction, rejectInvoice, updateInvoice, uploadInvoiceFile,
  waiveException, type InvoiceData,
} from '../../api/invoices'
import { getInvoiceApprovals, reviseInvoice, sendForApproval } from '../../api/approvals'
import { useAction } from '../../components/Busy'
import { FindingText, RuleName } from '../../components/Findings'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDate, formatMoney, formatNumber, type CalendarPreference } from '../../i18n/format'
import { ApprovalHistory } from './ApprovalHistory'
import { InvoiceEditor } from './InvoiceEditor'

export function InvoiceDetailPage({ calendar }: { calendar: CalendarPreference }) {
  const { id = '' } = useParams()
  const { t, i18n } = useTranslation()
  const invoice = useLoad(() => getInvoice(id), id)
  const verification = useLoad(() => getVerification(id), id)
  const extractions = useLoad(() => getExtractions(id), id)
  const approvals = useLoad(() => getInvoiceApprovals(id), id)
  const act = useAction()
  const [reason, setReason] = useState('')
  const [editing, setEditing] = useState(false)
  const [revising, setRevising] = useState(false)
  const [reviseReason, setReviseReason] = useState('')
  const [reviseResult, setReviseResult] = useState<{ material: boolean; changedFields: string[] }>()

  if (invoice.loading && !invoice.data) return <p role="status">{t('common.loading')}</p>
  if (!invoice.data) return <ProblemAlert error={invoice.error ?? new Error('load')} />
  const inv = invoice.data
  const editable = inv.status === 'Draft' || inv.status === 'Exception' || inv.status === 'Returned'
  const revisable = inv.status === 'InApproval' || inv.status === 'Approved'
  const reloadAll = () => {
    invoice.reload()
    verification.reload()
    extractions.reload()
    approvals.reload()
  }
  const run = async (fn: () => Promise<unknown>) => {
    if ((await act.run(fn)) !== undefined) {
      setReason('')
      reloadAll()
    }
  }

  return (
    <div className="stack">
      <p><Link to="/invoices">{t('invoices.back')}</Link></p>
      <section className="card" aria-labelledby="invd-title">
        <h1 id="invd-title" dir="ltr">{inv.invoiceNumber || t('invoices.noNumber')}</h1>
        <dl>
          <dt>{t('vendors.status')}</dt>
          <dd>{t(`invoiceStatus.${inv.status}`, { defaultValue: inv.status })}</dd>
          <dt>{t('invoices.vendor')}</dt>
          <dd>{inv.vendorName}</dd>
          <dt>{t('invoices.date')}</dt>
          <dd>{formatDate(inv.invoiceDate, i18n.language, calendar)}</dd>
          <dt>{t('invoices.total')}</dt>
          <dd>{formatMoney(inv.total, inv.currencyCode, i18n.language)}</dd>
          <dt>{t('invoices.source')}</dt>
          <dd>{t(`invoiceSource.${inv.source}`, { defaultValue: inv.source })}</dd>
          <dt>{t('invoices.version')}</dt>
          <dd>{inv.version}</dd>
          {inv.statusReason && (<><dt>{t('vendors.reason')}</dt><dd>{inv.statusReason}</dd></>)}
        </dl>
        <ProblemAlert error={act.error} />
        <div className="row">
          {inv.status === 'Draft' && <button type="button" disabled={act.busy} onClick={() => void run(() => invoiceAction(id, 'submit'))}>{t('invoices.submit')}</button>}
          {inv.status === 'Verified' && (
            <button type="button" disabled={act.busy} onClick={() => void run(() => sendForApproval(id))}>{t('approvals.send')}</button>
          )}
          {revisable && <button type="button" className="secondary" onClick={() => setRevising(!revising)}>{t('approvals.revise')}</button>}
          {(inv.status === 'Exception' || inv.status === 'Verified') && (
            <button type="button" disabled={act.busy} onClick={() => void run(() => invoiceAction(id, 'reverify'))}>{t('invoices.reverify')}</button>
          )}
          {editable && <button type="button" className="secondary" onClick={() => setEditing(!editing)}>{t('invoices.edit')}</button>}
          <label>{t('vendors.reason')}<input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} /></label>
          {['Draft', 'Received', 'Exception', 'Verified', 'Returned'].includes(inv.status) && (
            <button type="button" className="secondary" disabled={act.busy || !reason.trim()} onClick={() => void run(() => cancelInvoice(id, reason))}>{t('invoices.cancel')}</button>
          )}
          {['Received', 'Exception', 'Verified', 'Returned'].includes(inv.status) && (
            <button type="button" className="secondary" disabled={act.busy || !reason.trim()} onClick={() => void run(() => rejectInvoice(id, reason))}>{t('invoices.reject')}</button>
          )}
        </div>
        {editing && editable && (
          <InvoiceEditor fixedTarget initial={inv as InvoiceData} onSave={async (data) => { await updateInvoice(id, data); setEditing(false); reloadAll() }} />
        )}
        {revising && revisable && (
          <div className="stack">
            <p className="muted">{t('approvals.reviseHelp')}</p>
            <label>{t('approvals.reviseReason')}<input value={reviseReason} onChange={(e) => setReviseReason(e.target.value)} maxLength={500} /></label>
            <InvoiceEditor
              fixedTarget
              initial={inv as InvoiceData}
              onSave={async (data) => {
                const r = await reviseInvoice(id, data, reviseReason)
                setReviseResult(r)
                setRevising(false)
                setReviseReason('')
                reloadAll()
              }}
            />
          </div>
        )}
        {reviseResult && (
          <p role="status">
            {reviseResult.material
              ? t('approvals.revisedMaterial', { fields: reviseResult.changedFields.map((f) => t(`materiality.field.${f}`, { defaultValue: f })).join(', ') })
              : t('approvals.revisedMinor')}
          </p>
        )}
      </section>

      {approvals.data && approvals.data.length > 0 && (
        <section className="card" aria-labelledby="inv-approvals">
          <h2 id="inv-approvals">{t('approvals.history')}</h2>
          {approvals.data.map((r, n) => (
            <div key={r.id}>
              {approvals.data!.length > 1 && <h3>{t('approvals.runNumber', { n: approvals.data!.length - n })}</h3>}
              <ApprovalHistory run={r} calendar={calendar} />
            </div>
          ))}
        </section>
      )}

      <section className="card" aria-labelledby="inv-lines">
        <h2 id="inv-lines">{t('invoices.lines')}</h2>
        {inv.lines.length === 0 && <p>{t('invoices.noLines')}</p>}
        {inv.lines.length > 0 && (
          <div className="table-wrap" tabIndex={0}>
            <table>
              <thead>
                <tr>
                  <th scope="col">{t('invoices.lineDescription')}</th><th scope="col">{t('invoices.quantity')}</th><th scope="col">{t('invoices.unitPrice')}</th>
                  <th scope="col">{t('invoices.lineAmount')}</th><th scope="col">{t('invoices.taxCode')}</th><th scope="col">{t('invoices.taxAmount')}</th>
                </tr>
              </thead>
              <tbody>
                {inv.lines.map((l, n) => (
                  <tr key={n}>
                    <td>{l.description}</td><td dir="ltr">{formatNumber(l.quantity, i18n.language)}</td><td dir="ltr">{formatMoney(l.unitPrice, inv.currencyCode, i18n.language)}</td>
                    <td dir="ltr">{formatMoney(l.lineAmount, inv.currencyCode, i18n.language)}</td>
                    <td dir="ltr">{l.taxCode ?? '—'}</td><td dir="ltr">{formatMoney(l.taxAmount, inv.currencyCode, i18n.language)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="card" aria-labelledby="inv-verif">
        <h2 id="inv-verif">{t('verification.title')}</h2>
        {verification.data && verification.data.results.length === 0 && <p>{t('verification.notYet')}</p>}
        <ul className="plain">
          {(verification.data?.results ?? []).map((r) => (
            <li key={r.ruleCode}>
              <strong><RuleName code={r.ruleCode} /></strong> — {t(`outcome.${r.outcome}`, { defaultValue: r.outcome })}
              {r.outcome === 'Failed' && <> ({t(`severity.${r.severity}`)})</>}
              {r.findings.length > 0 && (
                <ul className="plain">{r.findings.map((f, n) => (<li key={n}><FindingText finding={f} /></li>))}</ul>
              )}
            </li>
          ))}
        </ul>
        {(verification.data?.exceptions ?? []).filter((e) => e.status === 'Open').map((e) => (
          <WaiveRow key={e.id} exceptionId={e.id} label={<RuleName code={e.ruleCode} />} severity={e.severity} onDone={reloadAll} />
        ))}
      </section>

      <section className="card" aria-labelledby="inv-files">
        <h2 id="inv-files">{t('invoices.files')}</h2>
        <ul className="plain">
          {inv.attachments.map((a) => (
            <li key={a.id} className="row">
              <span dir="ltr">{a.fileName}</span>
              <span>{t(`attachmentRole.${a.role}`, { defaultValue: a.role })}</span>
              <span>{t(`scan.${a.scanStatus}`, { defaultValue: a.scanStatus })}</span>
              {a.scanStatus === 'Clean' && <a href={`/api/v1/invoices/${id}/attachments/${a.id}/file`}>{t('portal.download')}</a>}
            </li>
          ))}
        </ul>
        {editable && <UploadForm invoiceId={id} onDone={reloadAll} />}
        {(extractions.data ?? []).filter((x) => x.status === 'Suggested').map((x) => (
          <div key={x.id} className="card inner">
            <h3>{t('invoices.suggestions')}</h3>
            <p className="muted">{t('invoices.suggestionsHelp')}</p>
            {Object.keys(x.result.fields).length === 0 && <p>{t('invoices.noSuggestions')}</p>}
            <ul className="plain">
              {Object.entries(x.result.fields).map(([k, v]) => (
                <li key={k}>{t(`suggestionField.${k}`, { defaultValue: k })}: <span dir="ltr">{v.value}</span> ({Math.round(v.confidence * 100)}%)</li>
              ))}
            </ul>
            <button type="button" className="secondary" onClick={() => void run(() => dismissExtraction(id, x.id))}>{t('invoices.dismissSuggestion')}</button>
          </div>
        ))}
      </section>
    </div>
  )
}

function WaiveRow({ exceptionId, label, severity, onDone }: { exceptionId: string; label: React.ReactNode; severity: string; onDone: () => void }) {
  const { t } = useTranslation()
  const act = useAction()
  const [reason, setReason] = useState('')
  async function submit(e: FormEvent) {
    e.preventDefault()
    if ((await act.run(() => waiveException(exceptionId, reason))) !== undefined) onDone()
  }
  return (
    <form onSubmit={submit} className="row">
      <span>{label} ({t(`severity.${severity}`)})</span>
      <label>{t('exceptions.waiveReason')}<input value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} /></label>
      <button type="submit" disabled={act.busy || !reason.trim()}>{t('exceptions.waive')}</button>
      <ProblemAlert error={act.error} />
    </form>
  )
}

function UploadForm({ invoiceId, onDone }: { invoiceId: string; onDone: () => void }) {
  const { t } = useTranslation()
  const act = useAction()
  const [role, setRole] = useState('Supporting')
  const [file, setFile] = useState<File>()
  async function submit(e: FormEvent) {
    e.preventDefault()
    if (file && (await act.run(() => uploadInvoiceFile(invoiceId, role, file))) !== undefined) {
      setFile(undefined)
      onDone()
    }
  }
  return (
    <form onSubmit={submit} className="row">
      <label>
        {t('invoices.attachmentRole')}
        <select value={role} onChange={(e) => setRole(e.target.value)}>
          <option value="Original">{t('attachmentRole.Original')}</option>
          <option value="Supporting">{t('attachmentRole.Supporting')}</option>
        </select>
      </label>
      <label>{t('portal.file')}<input type="file" accept=".pdf,.png,.jpg,.jpeg" onChange={(e) => setFile(e.target.files?.[0])} /></label>
      <button type="submit" disabled={act.busy || !file}>{t('portal.upload')}</button>
      <ProblemAlert error={act.error} />
    </form>
  )
}
