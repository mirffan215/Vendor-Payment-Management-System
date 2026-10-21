import { api, setCsrfToken } from './client'

const P = '/api/v1/portal'
const json = (body: unknown): RequestInit => ({ headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
const post = <T>(path: string, body: unknown = {}) => api<T>(path, { method: 'POST', ...json(body) })

export interface InviteInfo {
  email: string
  displayName: string
  vendorName: string
  purpose: string
  needsAuthenticator: boolean
}
export interface Enrollment {
  secret: string
  provisioningUri: string
}
export interface PortalSessionInfo {
  userId: string
  vendorId: string
  vendorName?: string | null
  email: string
  displayName: string
  csrfToken: string
}
export interface PortalProfile {
  id: string
  legalName: string
  legalNameAr?: string | null
  tradeName?: string | null
  countryCode: string
  contactEmail?: string | null
  contactPhone?: string | null
  status: string
  verificationStatus: string
}
export interface PortalEntityLink {
  legalEntityId: string
  legalEntityName?: string | null
  paymentTermsCode?: string | null
  defaultCurrency: string
  status: string
}
export interface PortalDocument {
  id: string
  type: string
  fileName: string
  scanStatus: string
  status: string
  reviewNote?: string | null
  createdAt: string
}
export interface PortalBank {
  id: string
  bankName: string
  accountName: string
  currencyCode: string
  maskedAccount: string
  status: string
}
export interface PortalBankChange {
  id: string
  status: string
  reason: string
  madeAt: string
  expiresAt: string
  proposed: PortalBank
}
export interface PortalRequest {
  id: string
  subject: string
  status: string
  createdAt: string
  messages: { id: string; direction: string; body: string; createdAt: string }[]
}

export const getInvite = (tenant: string, token: string) =>
  api<InviteInfo>(`/portal/auth/invite?tenant=${encodeURIComponent(tenant)}&token=${encodeURIComponent(token)}`)
export const acceptInvite = (tenant: string, token: string, password: string) =>
  post<{ completed: boolean; enrollment?: Enrollment | null }>('/portal/auth/invite/accept', { tenant, token, password })
export const enrollAuthenticator = (tenant: string, token: string, code: string) =>
  post<{ recoveryCodes: string[] }>('/portal/auth/invite/enroll', { tenant, token, code })
export const portalLogin = (tenant: string, email: string, password: string) =>
  post<{ challenge: string }>('/portal/auth/login', { tenant, email, password })
export const portalMfa = (challenge: string, code: string) => post<void>('/portal/auth/mfa', { challenge, code })
export const portalLogout = () => api<void>('/portal/auth/logout', { method: 'POST' })

export async function getPortalSession(): Promise<PortalSessionInfo> {
  const session = await api<PortalSessionInfo>(`${P}/session`)
  setCsrfToken(session.csrfToken)
  return session
}
export const getProfile = () => api<PortalProfile>(`${P}/profile`)
export const updateProfile = (body: { legalNameAr: string; tradeName: string; contactEmail: string; contactPhone: string }) =>
  api<PortalProfile>(`${P}/profile`, { method: 'PUT', ...json(body) })
export const getEntities = () => api<PortalEntityLink[]>(`${P}/entities`)
export const getDocuments = () => api<PortalDocument[]>(`${P}/documents`)
export const uploadDocument = (type: string, file: File) => {
  const form = new FormData()
  form.append('type', type)
  form.append('file', file)
  return api<PortalDocument>(`${P}/documents`, { method: 'POST', body: form })
}
export const getBankDetails = () => api<PortalBank[]>(`${P}/bank-details`)
export const getBankChanges = () => api<PortalBankChange[]>(`${P}/bank-detail-changes`)
export const proposeBank = (body: Record<string, string>) => post<PortalBankChange>(`${P}/bank-detail-changes`, body)
export const cancelBankChange = (id: string) => post<PortalBankChange>(`${P}/bank-detail-changes/${id}/cancel`)
export const getRequests = () => api<PortalRequest[]>(`${P}/requests`)
export const replyToRequest = (id: string, body: string) => post<PortalRequest>(`${P}/requests/${id}/reply`, { body })

export interface PortalInvoice {
  id: string
  invoiceNumber: string
  invoiceDate: string
  dueDate?: string | null
  currencyCode: string
  total: number
  poReference?: string | null
  status: string
  entityName?: string | null
  legalEntityId: string
  createdAt: string
}
export const getPortalInvoices = () => api<PortalInvoice[]>(`${P}/invoices`)
export const submitPortalInvoice = (fields: Record<string, string>, file: File) => {
  const form = new FormData()
  for (const [k, v] of Object.entries(fields)) if (v !== '') form.append(k, v)
  form.append('file', file)
  return api<PortalInvoice>(`${P}/invoices`, { method: 'POST', body: form })
}
