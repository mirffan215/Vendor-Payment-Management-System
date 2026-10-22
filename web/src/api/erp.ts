import { api } from './client'

const V1 = '/api/v1'
const json = (body: unknown): RequestInit => ({ headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
const post = <T>(path: string, body: unknown = {}) => api<T>(`${V1}${path}`, { method: 'POST', ...json(body) })
const put = <T>(path: string, body: unknown) => api<T>(`${V1}${path}`, { method: 'PUT', ...json(body) })
const get = <T>(path: string) => api<T>(`${V1}${path}`)

export const ENTITY_SET_ROLES = [
  'vendors', 'invoiceJournalHeaders', 'invoiceJournalLines', 'invoiceAttachments', 'vendorInvoices', 'vendorPayments', 'purchaseOrders', 'purchaseOrderLines', 'dimensionValues',
] as const

export interface ConnectionConfig {
  provider: string
  baseUrl: string
  authorityTenantId: string
  clientId: string
  secretRef: string
  entitySets: Record<string, string>
  pageSize: number
  timeoutSeconds: number
}

export interface DimensionMap { vpmsDimension: string; erpDimension: string; defaultValue: string | null; valueMap: Record<string, string> }

export interface MappingConfig {
  company: string
  journalName: string
  postingProfile: string | null
  paymentTermsMap: Record<string, string>
  taxCodeMap: Record<string, string>
  dimensions: DimensionMap[]
}

export type ConfigStatus = 'Active' | 'Pending' | 'Superseded' | 'Rejected' | 'Cancelled'

export interface ErpConfig {
  id: string | null
  kind: 'Connection' | 'Mapping'
  legalEntityId: string | null
  versionNo: number
  status: ConfigStatus
  connection: ConnectionConfig | null
  mapping: MappingConfig | null
  note: string
  createdBy: string | null
  createdAt: string | null
  decidedBy: string | null
  decidedAt: string | null
  decisionComment: string | null
}

export interface ConfigState { active: ErpConfig | null; pending: ErpConfig | null }
export interface ConfigChange { change: ErpConfig; base: ErpConfig | null }
export interface ConnectionTest { ok: boolean; errorCode: string | null; message: string | null }

export const getConnection = () => get<ConfigState>('/admin/erp/connection')
export const proposeConnection = (config: ConnectionConfig, note: string) => put<ErpConfig>('/admin/erp/connection', { config, note })
export const getMapping = (entityId: string) => get<ConfigState>(`/admin/legal-entities/${entityId}/erp-mapping`)
export const proposeMapping = (entityId: string, config: MappingConfig, note: string) => put<ErpConfig>(`/admin/legal-entities/${entityId}/erp-mapping`, { config, note })
export const testConnection = (entityId: string) => post<ConnectionTest>(`/admin/legal-entities/${entityId}/erp/test`)
export const listConfigChanges = (status = 'Pending') => get<ConfigChange[]>(`/erp-changes?status=${status}`)
export const approveConfigChange = (id: string, comment: string) => post<ErpConfig>(`/erp-changes/${id}/approve`, { comment })
export const rejectConfigChange = (id: string, comment: string) => post<ErpConfig>(`/erp-changes/${id}/reject`, { comment })
export const cancelConfigChange = (id: string) => post<ErpConfig>(`/erp-changes/${id}/cancel`)

// --- sync monitor ----------------------------------------------------------------------------------------------

export type SyncStatus = 'Pending' | 'InFlight' | 'Succeeded' | 'DeadLetter' | 'Resolved' | 'Superseded'

export interface SyncSummary { pending: number; inFlight: number; succeeded: number; deadLetter: number; resolved: number }

export interface SyncRecord {
  id: string
  legalEntityId: string
  type: 'PostInvoice' | 'UpsertVendor'
  objectId: string
  objectVersion: string
  status: SyncStatus
  attempts: number
  nextAttemptAt: string
  lastErrorCode: string | null
  lastError: string | null
  erpReference: string | null
  createdAt: string
  completedAt: string | null
  handledBy: string | null
  handlingNote: string | null
}

export interface SyncAttempt { attemptNo: number; at: string; outcome: string; code: string | null; message: string | null }
export interface SyncDetail { record: SyncRecord; attempts: SyncAttempt[] }

export const getSyncSummary = () => get<SyncSummary>('/erp/sync/summary')
export const listSync = (status?: string) => get<SyncRecord[]>(`/erp/sync${status ? `?status=${status}` : ''}`)
export const getSyncDetail = (id: string) => get<SyncDetail>(`/erp/sync/${id}`)
export const retrySync = (id: string, reason: string) => post<SyncRecord>(`/erp/sync/${id}/retry`, { reason })
export const resolveSync = (id: string, reason: string, erpReference: string | null) => post<SyncRecord>(`/erp/sync/${id}/resolve`, { reason, erpReference })

// --- reconciliation --------------------------------------------------------------------------------------------

export type ReconType = 'MissingInErp' | 'AmountMismatch' | 'VendorMismatch' | 'StatusMismatch' | 'UnknownInErp' | 'PaidInErpNotReleased'
export type ReconStatus = 'Open' | 'PendingAcceptance' | 'Accepted' | 'Resolved' | 'Cleared'

export const RECON_TYPES: ReconType[] = ['MissingInErp', 'AmountMismatch', 'VendorMismatch', 'StatusMismatch', 'UnknownInErp', 'PaidInErpNotReleased']

export interface ReconRun {
  id: string
  legalEntityId: string
  startedAt: string
  completedAt: string | null
  status: 'Completed' | 'Failed'
  errorCode: string | null
  vpmsInvoices: number
  erpDocuments: number
  newItems: number
  openItems: number
  clearedItems: number
}

export interface ReconItem {
  id: string
  legalEntityId: string
  type: ReconType
  invoiceId: string | null
  invoiceNumber: string | null
  erpReference: string | null
  vpmsValue: string
  erpValue: string
  status: ReconStatus
  firstSeenAt: string
  lastSeenAt: string
  proposedBy: string | null
  proposedReason: string | null
  decidedBy: string | null
  decidedAt: string | null
  decisionReason: string | null
}

export interface ReconSummary { open: number; pendingAcceptance: number; accepted: number; resolved: number; openByType: Record<string, number> }

export const runReconciliation = (legalEntityId: string) => post<ReconRun>('/erp/reconciliation/run', { legalEntityId })
export const getReconSummary = () => get<ReconSummary>('/erp/reconciliation/summary')
export const listReconRuns = (legalEntityId?: string) => get<ReconRun[]>(`/erp/reconciliation/runs${legalEntityId ? `?legalEntityId=${legalEntityId}` : ''}`)
export const listReconItems = (status?: string, type?: string) => {
  const q = new URLSearchParams()
  if (status) q.set('status', status)
  if (type) q.set('type', type)
  const s = q.toString()
  return get<ReconItem[]>(`/erp/reconciliation/items${s ? `?${s}` : ''}`)
}
export const resolveReconItem = (id: string, reason: string) => post<ReconItem>(`/erp/reconciliation/items/${id}/resolve`, { reason })
export const proposeReconAcceptance = (id: string, reason: string) => post<ReconItem>(`/erp/reconciliation/items/${id}/propose-acceptance`, { reason })
export const approveReconAcceptance = (id: string, comment: string) => post<ReconItem>(`/erp/reconciliation/items/${id}/approve-acceptance`, { comment })
export const rejectReconAcceptance = (id: string, reason: string) => post<ReconItem>(`/erp/reconciliation/items/${id}/reject-acceptance`, { reason })
export const reconExportUrl = (status?: string) => `${V1}/erp/reconciliation/export${status ? `?status=${status}` : ''}`

/** "KEY=VALUE" lines <-> a map, for the small mapping tables. */
export function textToMap(text: string): Record<string, string> {
  const map: Record<string, string> = {}
  for (const line of text.split('\n')) {
    const at = line.indexOf('=')
    if (at < 1) continue
    const key = line.slice(0, at).trim()
    const value = line.slice(at + 1).trim()
    if (key && value) map[key] = value
  }
  return map
}

export const mapToText = (map: Record<string, string> | undefined) => Object.entries(map ?? {}).map(([k, v]) => `${k}=${v}`).join('\n')
