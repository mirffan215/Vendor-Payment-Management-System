import { api } from './client'

const V1 = '/api/v1/payments'
const json = (body: unknown): RequestInit => ({ headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
const post = <T>(path: string, body: unknown = {}) => api<T>(`${V1}${path}`, { method: 'POST', ...json(body) })
const get = <T>(path: string) => api<T>(`${V1}${path}`)

export interface PayableInvoice {
  invoiceId: string
  legalEntityId: string
  vendorId: string
  vendorName: string
  invoiceNumber: string
  invoiceDate: string
  dueDate: string
  currencyCode: string
  total: number
  approvedAt: string | null
  hasOpenRequest: boolean
  blockers: string[]
}

export interface PaymentRequest {
  id: string
  legalEntityId: string
  vendorId: string
  vendorName: string
  invoiceId: string
  invoiceNumber: string
  invoiceVersion: number
  amount: number
  currencyCode: string
  dueDate: string
  status: 'Created' | 'InProposal' | 'Released' | 'Invalidated' | 'Cancelled'
  statusReason: string | null
  proposalId: string | null
  createdAt: string
}

export interface RequestResult { invoiceId: string; request: PaymentRequest | null; errorCode: string | null }

export interface ProposalItem { paymentRequestId: string; invoiceId: string; invoiceNumber: string; amount: number }

export interface Proposal {
  id: string
  legalEntityId: string
  proposalNo: string
  vendorId: string
  vendorName: string
  dueDate: string
  currencyCode: string
  totalAmount: number
  status: 'Draft' | 'PendingApproval' | 'Approved' | 'Released' | 'Invalidated' | 'Cancelled'
  statusReason: string | null
  createdBy: string
  createdAt: string
  submittedBy: string | null
  approvedBy: string | null
  approvedAt: string | null
  approvalComment: string | null
  releasedBy: string | null
  releasedAt: string | null
  items: ProposalItem[]
}

export interface RecheckFinding { paymentRequestId: string; invoiceId: string; code: string; severity: 'Invalidates' | 'Blocks' }
export interface Recheck { passed: boolean; invalidates: boolean; findings: RecheckFinding[] }

export interface Instruction {
  id: string
  proposalId: string
  proposalNo: string
  format: string
  hash: string
  signature: string
  itemCount: number
  totalAmount: number
  currencyCode: string
  status: 'Prepared' | 'HandedOff'
  preparedBy: string
  preparedAt: string
  handedOffBy: string | null
  handedOffAt: string | null
}

export interface Authorization {
  id: string
  authorizedBy: string
  authorizedAt: string
  decision: 'Released' | 'Denied'
  denialReason: string | null
  recheck: RecheckFinding[]
  separation: { code: string }[]
}

export const getPayable = (legalEntityId?: string) => get<PayableInvoice[]>(`/payable${legalEntityId ? `?legalEntityId=${legalEntityId}` : ''}`)
export const getRequests = (status = '') => get<PaymentRequest[]>(`/requests?take=100${status ? `&status=${status}` : ''}`)
export const createRequests = (invoiceIds: string[]) => post<RequestResult[]>('/requests', { invoiceIds })
export const cancelRequest = (id: string, reason: string) => post<PaymentRequest>(`/requests/${id}/cancel`, { reason })

export const getProposals = (status = '') => get<Proposal[]>(`/proposals?take=100${status ? `&status=${status}` : ''}`)
export const getProposal = (id: string) => get<Proposal>(`/proposals/${id}`)
export const createProposals = (legalEntityId: string, dueOnOrBefore: string | null, requestIds: string[] | null) =>
  post<Proposal[]>('/proposals', { legalEntityId, dueOnOrBefore, currencyCode: null, vendorId: null, requestIds })
export const getRecheck = (id: string) => get<Recheck>(`/proposals/${id}/recheck`)
export const proposalAction = (id: string, action: 'submit' | 'approve' | 'send-back' | 'cancel' | 'release', body: Record<string, unknown> = {}) =>
  post<unknown>(`/proposals/${id}/${action}`, body)
export const removeProposalRequest = (id: string, requestId: string) => api<Proposal>(`${V1}/proposals/${id}/requests/${requestId}`, { method: 'DELETE' })
export const getInstruction = (proposalId: string) => get<Instruction>(`/proposals/${proposalId}/instruction`)
export const getAuthorizations = (proposalId: string) => get<Authorization[]>(`/proposals/${proposalId}/authorizations`)
export const verifyInstruction = (id: string) => get<{ hashMatches: boolean; signatureMatches: boolean; valid: boolean }>(`/instructions/${id}/verify`)
export const instructionUrl = (id: string, kind: 'csv' | 'json') => `${V1}/instructions/${id}/download?kind=${kind}`
