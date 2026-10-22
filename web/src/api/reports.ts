import { api } from './client'

const V1 = '/api/v1'

export type CellValue = string | number | boolean | null

export interface ReportColumn { key: string; label: string; type: 'text' | 'int' | 'number' | 'money' | 'date' | 'datetime' | 'flag' }
export interface ReportTable { name: string; title: string; columns: ReportColumn[]; rows: CellValue[][] }

export interface ReportResult {
  key: string
  title: string
  locale: string
  calendar: string
  generatedAt: string
  filters: Record<string, string>
  truncated: boolean
  tables: ReportTable[]
}

export interface ReportInfo { key: string; title: string; description: string; needsPeriod: boolean; needsTaxPermission: boolean }

export interface ReportQuery { legalEntityId?: string; from?: string; to?: string; asOf?: string; locale: string; calendar: string }

function query(q: ReportQuery, extra: Record<string, string> = {}): string {
  const p = new URLSearchParams()
  if (q.legalEntityId) p.set('legalEntityId', q.legalEntityId)
  if (q.from) p.set('from', q.from)
  if (q.to) p.set('to', q.to)
  if (q.asOf) p.set('asOf', q.asOf)
  p.set('locale', q.locale)
  p.set('calendar', q.calendar)
  for (const [k, v] of Object.entries(extra)) p.set(k, v)
  return p.toString()
}

export const getReportCatalog = (locale: string) => api<ReportInfo[]>(`${V1}/reports?locale=${locale}`)
export const runReport = (report: string, q: ReportQuery) => api<ReportResult>(`${V1}/reports/${report}?${query(q)}`)
export const reportExportUrl = (report: string, q: ReportQuery, format: 'csv' | 'xlsx' | 'pdf', part?: string) =>
  `${V1}/reports/${report}/export?${query(q, { format, ...(part ? { part } : {}) })}`
