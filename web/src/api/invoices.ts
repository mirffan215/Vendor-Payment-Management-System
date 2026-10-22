import { api } from './client'

const V1 = '/api/v1'
const json = (body: unknown): RequestInit => ({ headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
const post = <T>(path: string, body: unknown = {}) => api<T>(`${V1}${path}`, { method: 'POST', ...json(body) })
const put = <T>(path: string, body: unknown) => api<T>(`${V1}${path}`, { method: 'PUT', ...json(body) })
const get = <T>(path: string) => api<T>(`${V1}${path}`)

export interface InvoiceLineInput {
  description: string
  quantity: number
  unitPrice: number
  lineAmount: number
  taxCode: string | null
  taxRate: number
  taxAmount: number
  poLineRef: string | null
  dimensionsJson: string | null
}
export interface InvoiceData {
  invoiceNumber: string
  invoiceDate: string
  dueDate: string | null
  currencyCode: string
  subtotal: number
  taxTotal: number
  total: number
  poReference: string | null
  supplierTaxInvoiceRef: string | null
  lines: InvoiceLineInput[]
  supplierTaxNumber: string | null
}
export interface InvoiceAttachment {
  id: string
  attachmentId: string
  role: string
  fileName: string
  scanStatus: string
  sizeBytes: number
}
export interface InvoiceDto extends InvoiceData {
  id: string
  legalEntityId: string
  vendorId: string
  vendorName: string
  source: string
  status: string
  statusReason?: string | null
  version: number
  createdAt: string
  receivedAt?: string | null
  attachments: InvoiceAttachment[]
}
export interface InvoiceListItem {
  id: string
  legalEntityId: string
  vendorId: string
  vendorName: string
  invoiceNumber: string
  invoiceDate: string
  currencyCode: string
  total: number
  source: string
  status: string
  createdAt: string
}
export interface Finding {
  key: string
  evidence: Record<string, string>
}
export interface VerificationResult {
  ruleCode: string
  outcome: string
  severity: string
  findings: Finding[]
}
export interface ExceptionDto {
  id: string
  invoiceId: string
  invoiceNumber: string
  legalEntityId: string
  ruleCode: string
  severity: string
  status: string
  findings: Finding[]
  invoiceVersion: number
  assignedTo?: string | null
  resolution?: string | null
  createdAt: string
}
export interface VerificationDetail {
  invoiceId: string
  invoiceStatus: string
  invoiceVersion: number
  ruleSetVersion: number
  results: VerificationResult[]
  exceptions: ExceptionDto[]
}
export interface Suggestion {
  value: string
  confidence: number
}
export interface ExtractionDto {
  id: string
  engine: string
  status: string
  result: { engine: string; fields: Record<string, Suggestion>; lines: unknown[] }
}

export interface RuleSetting {
  enabled: boolean
  severity: string
  parameters: Record<string, number>
}
export interface RuleSetDto {
  id: string | null
  legalEntityId: string
  versionNo: number
  status: string
  config: { rules: Record<string, RuleSetting> }
  note: string
  loosenedRules: string[]
  createdBy?: string | null
  createdAt?: string | null
}
export interface EntityRules {
  active: RuleSetDto
  pending: RuleSetDto | null
}
export interface RuleChange {
  change: RuleSetDto
  base: RuleSetDto
}
export interface RuleDefinition {
  code: string
  defaultEnabled: boolean
  defaultSeverity: string
  canDisable: boolean
  canFlag: boolean
  parameters: { name: string; default: number; min: number; max: number }[]
}

export interface IntakeMessage {
  id: string
  legalEntityId: string
  fromAddress: string
  subject: string
  receivedAt: string
  status: string
  vendorId?: string | null
  invoiceId?: string | null
  attempts: number
  note?: string | null
  attachmentCount: number
}
export interface Mailbox {
  legalEntityId: string
  address: string
  enabled: boolean
  lastPolledAt?: string | null
  lastError?: string | null
}
export interface PurchaseOrder {
  id: string
  legalEntityId: string
  poNumber: string
  version: number
  currencyCode: string
  status: string
  orderDate: string
  lines: { lineNo: string; description: string; orderedQuantity: number; unitPrice: number }[]
}
export interface PoImportResult {
  created: number
  updated: number
  unchanged: number
  errors: { poNumber: string; code: string; message: string }[]
}

export const listInvoices = (q: { status?: string; search?: string; legalEntityId?: string; source?: string }) => {
  const p = new URLSearchParams({ take: '100' })
  for (const [k, v] of Object.entries(q)) if (v) p.set(k, v)
  return get<InvoiceListItem[]>(`/invoices?${p}`)
}
export const getInvoice = (id: string) => get<InvoiceDto>(`/invoices/${id}`)
export const createInvoice = (legalEntityId: string, vendorId: string, data: InvoiceData) => post<InvoiceDto>('/invoices', { legalEntityId, vendorId, data })
export const updateInvoice = (id: string, data: InvoiceData) => put<InvoiceDto>(`/invoices/${id}`, data)
export const invoiceAction = (id: string, action: 'submit' | 'reverify') => post<unknown>(`/invoices/${id}/${action}`)
export const cancelInvoice = (id: string, reason: string) => post<InvoiceDto>(`/invoices/${id}/cancel`, { reason })
export const rejectInvoice = (id: string, reason: string) => post<InvoiceDto>(`/invoices/${id}/reject`, { reason })
export const getVerification = (id: string) => get<VerificationDetail>(`/invoices/${id}/verification`)
export const getExtractions = (id: string) => get<ExtractionDto[]>(`/invoices/${id}/extractions`)
export const dismissExtraction = (id: string, extractionId: string) => post<void>(`/invoices/${id}/extractions/${extractionId}/dismiss`)
export const uploadInvoiceFile = (id: string, role: string, file: File) => {
  const form = new FormData()
  form.append('role', role)
  form.append('file', file)
  return api<InvoiceAttachment>(`${V1}/invoices/${id}/attachments`, { method: 'POST', body: form })
}
export const createFromDocument = (legalEntityId: string, vendorId: string, file: File) => {
  const form = new FormData()
  form.append('legalEntityId', legalEntityId)
  form.append('vendorId', vendorId)
  form.append('file', file)
  return api<InvoiceDto>(`${V1}/invoices/from-document`, { method: 'POST', body: form })
}

export const listExceptions = (status: string) => get<ExceptionDto[]>(`/invoice-exceptions?take=100${status ? `&status=${status}` : ''}`)
export const waiveException = (id: string, reason: string) => post<ExceptionDto>(`/invoice-exceptions/${id}/waive`, { reason })

export const getRuleCatalog = () => get<RuleDefinition[]>('/verification-rules/catalog')
export const getEntityRules = (entityId: string) => get<EntityRules>(`/admin/legal-entities/${entityId}/verification-rules`)
export const proposeRules = (entityId: string, rules: Record<string, RuleSetting>, note: string) =>
  put<RuleSetDto>(`/admin/legal-entities/${entityId}/verification-rules`, { rules, note })
export const listRuleChanges = (status: string) => get<RuleChange[]>(`/verification-rule-changes${status ? `?status=${status}` : ''}`)
export const decideRuleChange = (id: string, decision: 'approve' | 'reject' | 'cancel', comment?: string) =>
  post<RuleSetDto>(`/verification-rule-changes/${id}/${decision}`, { comment: comment ?? null })

export const listIntakeMessages = (status: string) => get<IntakeMessage[]>(`/invoice-intake/messages?take=100${status ? `&status=${status}` : ''}`)
export const assignIntakeMessage = (id: string, vendorId: string) => post<IntakeMessage>(`/invoice-intake/messages/${id}/assign`, { vendorId })
export const dismissIntakeMessage = (id: string, reason: string) => post<IntakeMessage>(`/invoice-intake/messages/${id}/dismiss`, { reason })
export const getMailbox = (entityId: string) => get<Mailbox | null>(`/admin/legal-entities/${entityId}/invoice-mailbox`)
export const saveMailbox = (entityId: string, address: string, enabled: boolean) => put<Mailbox>(`/admin/legal-entities/${entityId}/invoice-mailbox`, { address, enabled })

export const listPurchaseOrders = (entityId: string, search: string) =>
  get<PurchaseOrder[]>(`/purchase-orders?take=100&legalEntityId=${entityId}${search ? `&search=${encodeURIComponent(search)}` : ''}`)
export const importPurchaseOrders = (entityId: string, purchaseOrders: unknown[]) =>
  post<PoImportResult>(`/admin/legal-entities/${entityId}/purchase-orders/import`, { purchaseOrders })
