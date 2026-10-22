import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { getReportCatalog, reportExportUrl, runReport, type CellValue, type ReportColumn, type ReportInfo, type ReportQuery, type ReportResult } from '../../api/reports'
import { listEntities } from '../../api/staff'
import { useAction } from '../../components/Busy'
import { ProblemAlert } from '../../components/ProblemText'
import { useLoad } from '../../hooks/useLoad'
import { formatDate, formatDateTime, formatNumber, type CalendarPreference } from '../../i18n/format'

/** File type names are the same in every language. */
const FORMATS = [
  { format: 'csv', name: 'CSV', part: 'detail' },
  { format: 'xlsx', name: 'Excel', part: undefined },
  { format: 'pdf', name: 'PDF', part: undefined },
] as const

const today = () => new Date().toISOString().slice(0, 10)
const daysAgo = (n: number) => new Date(Date.now() - n * 86_400_000).toISOString().slice(0, 10)

export function ReportsPage({ calendar }: { calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  const catalog = useLoad(() => getReportCatalog(i18n.language), `catalog-${i18n.language}`)
  const entities = useLoad(listEntities, 'entities')
  const act = useAction()
  const [report, setReport] = useState('')
  const [entityId, setEntityId] = useState('')
  const [from, setFrom] = useState(daysAgo(30))
  const [to, setTo] = useState(today())
  const [asOf, setAsOf] = useState(today())
  const [result, setResult] = useState<ReportResult>()

  const current: ReportInfo | undefined = catalog.data?.find((r) => r.key === report) ?? catalog.data?.[0]
  const query = (): ReportQuery => ({
    legalEntityId: entityId || undefined,
    from: current?.needsPeriod ? from : undefined,
    to: current?.needsPeriod || current?.key === 'payment-forecast' ? to : undefined,
    asOf: current?.needsPeriod ? undefined : asOf,
    locale: i18n.language,
    calendar,
  })

  async function run() {
    if (!current) return
    const r = await act.run(() => runReport(current.key, query()))
    if (r) setResult(r)
  }

  return (
    <div className="stack">
      <section className="card" aria-labelledby="reports-title">
        <h1 id="reports-title">{t('reports.title')}</h1>
        <p>{t('reports.intro')}</p>
        <ProblemAlert error={catalog.error ?? entities.error ?? act.error} />
        {catalog.data?.length === 0 && <p>{t('reports.none')}</p>}
        {current && (
          <>
            <div className="row">
              <label>
                {t('reports.report')}
                <select value={current.key} onChange={(e) => { setReport(e.target.value); setResult(undefined) }}>
                  {(catalog.data ?? []).map((r) => (<option key={r.key} value={r.key}>{r.title}</option>))}
                </select>
              </label>
              <label>
                {t('invoices.entity')}
                <select value={entityId} onChange={(e) => setEntityId(e.target.value)}>
                  <option value="">{t('reports.allEntities')}</option>
                  {(entities.data ?? []).map((x) => (<option key={x.id} value={x.id}>{x.code} — {x.legalName}</option>))}
                </select>
              </label>
              {current.needsPeriod ? (
                <>
                  <label>{t('reports.from')}<input type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></label>
                  <label>{t('reports.to')}<input type="date" value={to} onChange={(e) => setTo(e.target.value)} /></label>
                </>
              ) : (
                <label>{t('reports.asOf')}<input type="date" value={asOf} onChange={(e) => setAsOf(e.target.value)} /></label>
              )}
            </div>
            <p className="muted">{current.description}</p>
            <button type="button" disabled={act.busy} onClick={() => void run()}>{t('reports.run')}</button>
          </>
        )}
      </section>

      {result && current && (
        <section className="card" aria-labelledby="report-result-title">
          <h2 id="report-result-title">{result.title}</h2>
          <p className="muted">
            {Object.entries(result.filters).map(([k, v]) => `${k}: ${v}`).join(' · ')} · {formatDateTime(result.generatedAt, i18n.language, calendar)}
          </p>
          {result.truncated && <p role="alert" className="alert">{t('reports.truncated')}</p>}
          <div className="row" aria-label={t('reports.export')}>
            <span>{t('reports.export')}:</span>
            {FORMATS.map((f) => (
              <a key={f.format} href={reportExportUrl(current.key, query(), f.format, f.part)} download>{f.name}</a>
            ))}
          </div>
          {result.tables.map((table) => (
            <div key={table.name}>
              <h3>{table.title}</h3>
              {table.rows.length === 0 ? <p>{t('reports.empty')}</p> : <ResultTable table={table} calendar={calendar} />}
            </div>
          ))}
        </section>
      )}
    </div>
  )
}

function ResultTable({ table, calendar }: { table: ReportResult['tables'][number]; calendar: CalendarPreference }) {
  const { t, i18n } = useTranslation()
  // Hijri companion columns are for exported files; on screen the date follows the person's calendar preference.
  const visible = table.columns.map((c, i) => ({ c, i })).filter(({ c }) => !c.key.endsWith('Hijri'))
  const cell = (v: CellValue, c: ReportColumn): string => {
    if (v === null || v === '') return ''
    if (typeof v === 'boolean') return t(v ? 'reports.yes' : 'reports.no')
    if (typeof v === 'number') return c.type === 'money' ? v.toLocaleString(i18n.language === 'ar' ? 'ar-SA-u-nu-latn' : 'en-GB', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) : formatNumber(v, i18n.language)
    if (c.type === 'date') return formatDate(v, i18n.language, calendar)
    if (c.type === 'datetime') return formatDateTime(v, i18n.language, calendar)
    return v
  }
  return (
    <div className="table-wrap" tabIndex={0}>
      <table>
        <thead><tr>{visible.map(({ c }) => (<th key={c.key} scope="col">{c.label}</th>))}</tr></thead>
        <tbody>
          {table.rows.map((row, r) => (
            <tr key={r}>{visible.map(({ c, i }) => (<td key={c.key} dir={c.type === 'text' ? undefined : 'ltr'}>{cell(row[i], c)}</td>))}</tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
