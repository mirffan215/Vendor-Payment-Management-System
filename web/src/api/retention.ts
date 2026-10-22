import { api } from './client'

const V1 = '/api/v1/admin'
const json = (body: unknown): RequestInit => ({ headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })

export const RETENTION_CLASSES = ['FinancialRecords', 'VendorData', 'Audit', 'Notifications', 'WorkflowNotices'] as const
export type RetentionClass = (typeof RETENTION_CLASSES)[number]

export interface RetentionSettings { months: Record<string, number>; countries: Record<string, Record<string, number>> }
export interface RetentionPolicy {
  id: string | null
  versionNo: number
  status: string
  settings: RetentionSettings
  note: string
  createdBy: string | null
  createdAt: string | null
  decidedBy: string | null
  decidedAt: string | null
  decisionComment: string | null
}
export interface RetentionState { active: RetentionPolicy | null; pending: RetentionPolicy | null; defaults: Record<string, number>; disposedAutomatically: string[] }
export interface RetentionReportRow { dataClass: string; months: number; cutoff: string; disposedAutomatically: boolean; eligibleCount: number | null; onHold: boolean }
export interface LegalHold {
  id: string
  scope: 'Tenant' | 'LegalEntity' | 'Vendor'
  legalEntityId: string | null
  vendorId: string | null
  reason: string
  placedBy: string
  placedAt: string
  releasedBy: string | null
  releasedAt: string | null
  releaseComment: string | null
}

export const getRetention = () => api<RetentionState>(`${V1}/retention`)
export const proposeRetention = (settings: RetentionSettings, note: string) => api<RetentionPolicy>(`${V1}/retention`, { method: 'PUT', ...json({ settings, note }) })
export const approveRetention = (id: string, comment: string) => api<RetentionPolicy>(`${V1}/retention/${id}/approve`, { method: 'POST', ...json({ comment }) })
export const rejectRetention = (id: string, comment: string) => api<RetentionPolicy>(`${V1}/retention/${id}/reject`, { method: 'POST', ...json({ comment }) })
export const cancelRetention = (id: string) => api<RetentionPolicy>(`${V1}/retention/${id}/cancel`, { method: 'POST', ...json({}) })
export const getRetentionReport = () => api<RetentionReportRow[]>(`${V1}/retention/report`)
export const listHolds = (activeOnly: boolean) => api<LegalHold[]>(`${V1}/legal-holds?activeOnly=${activeOnly}`)
export const placeHold = (hold: { scope: string; legalEntityId?: string; vendorId?: string; reason: string }) => api<LegalHold>(`${V1}/legal-holds`, { method: 'POST', ...json(hold) })
export const releaseHold = (id: string, comment: string) => api<LegalHold>(`${V1}/legal-holds/${id}/release`, { method: 'POST', ...json({ comment }) })
