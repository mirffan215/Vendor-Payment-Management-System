import { api } from './client'

const V1 = '/api/v1'
const json = (body: unknown): RequestInit => ({
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
})
const post = <T>(path: string, body: unknown = {}) => api<T>(`${V1}${path}`, { method: 'POST', ...json(body) })
const get = <T>(path: string) => api<T>(`${V1}${path}`)

export interface VendorListItem {
  id: string
  legalName: string
  countryCode: string
  status: string
  verificationStatus: string
  createdAt: string
}
export interface VendorDto {
  id: string
  legalName: string
  legalNameAr?: string | null
  tradeName?: string | null
  countryCode: string
  taxId?: string | null
  registrationNo?: string | null
  contactEmail?: string | null
  contactPhone?: string | null
  status: string
  verificationStatus: string
  statusReason?: string | null
  createdAt: string
}
export interface VendorDocumentDto {
  id: string
  type: string
  fileName: string
  scanStatus: string
  status: string
  reviewNote?: string | null
  expiresOn?: string | null
  createdAt: string
}
export interface EntityLinkDto {
  id: string
  legalEntityId: string
  paymentTermsCode?: string | null
  defaultCurrency: string
  status: string
}
export interface VendorDetails {
  vendor: VendorDto
  documents: VendorDocumentDto[]
  links: EntityLinkDto[]
}
export interface BankDetailsDto {
  id: string
  accountName: string
  bankName: string
  swiftBic?: string | null
  countryCode: string
  currencyCode: string
  maskedAccount: string
  status: string
  createdAt: string
  verifiedAt?: string | null
}
export interface BankChangeDto {
  id: string
  vendorId: string
  vendorName: string
  reason: string
  source: string
  madeAt: string
  expiresAt: string
  status: string
  requiresCallback: boolean
  proposed: BankDetailsDto
  current?: BankDetailsDto | null
}
export interface ProposeBank {
  accountName: string
  bankName: string
  swiftBic?: string
  countryCode: string
  currencyCode: string
  iban?: string
  accountNumber?: string
  reason: string
  confirmSharedAccount?: boolean
}
export interface RequestMessage {
  id: string
  direction: string
  body: string
  createdAt: string
}
export interface VendorRequestDto {
  id: string
  subject: string
  status: string
  createdAt: string
  messages: RequestMessage[]
}
export interface LegalEntityDto {
  id: string
  code: string
  legalName: string
  legalNameAr?: string | null
  countryCode: string
  baseCurrency: string
  status: string
  taxRegistrationNoMasked?: string | null
}
export interface UserDto {
  id: string
  email: string
  displayName: string
  status: string
  lastLoginAt?: string | null
  roles: string[]
}
export interface RoleDto {
  id: string
  name: string
  isSystem: boolean
  permissions: string[]
  assignmentCount: number
}
export interface ProviderDto {
  id: string
  kind: number
  displayName: string
  authority: string
  enabled: boolean
  allowedEmailDomains: string[]
}
export interface AuthorityDto {
  id: string
  legalEntityId: string
  userId: string
  maxAmount: number
  currencyCode: string
  validFrom: string
  validTo?: string | null
  status: string
}

export const listVendors = (status: string, search: string) =>
  get<VendorListItem[]>(`/vendors?take=100${status ? `&status=${encodeURIComponent(status)}` : ''}${search ? `&search=${encodeURIComponent(search)}` : ''}`)
export const createVendor = (body: { legalName: string; countryCode: string; taxId?: string }) => post<VendorDto>('/vendors', body)
export const getVendor = (id: string) => get<VendorDetails>(`/vendors/${id}`)
export const vendorAction = (id: string, action: string, reason?: string) =>
  post<VendorDto>(`/vendors/${id}/${action}`, reason === undefined ? {} : { reason })
export const verifyDocument = (vendorId: string, documentId: string) => post<VendorDocumentDto>(`/vendors/${vendorId}/documents/${documentId}/verify`)
export const rejectDocument = (vendorId: string, documentId: string, reason: string) =>
  post<VendorDocumentDto>(`/vendors/${vendorId}/documents/${documentId}/reject`, { reason })
export const listVendorBank = (id: string) => get<BankDetailsDto[]>(`/vendors/${id}/bank-details`)
export const proposeBank = (id: string, body: ProposeBank) => post<BankChangeDto>(`/vendors/${id}/bank-detail-changes`, body)
export const listVendorRequests = (id: string) => get<VendorRequestDto[]>(`/vendors/${id}/requests`)
export const createVendorRequest = (id: string, subject: string, body: string) => post<VendorRequestDto>(`/vendors/${id}/requests`, { subject, body })
export const inviteVendorUser = (id: string, email: string, displayName: string) =>
  post<{ inviteUrl: string }>(`/vendors/${id}/portal-users`, { email, displayName })

export const listBankChanges = (status: string) => get<BankChangeDto[]>(`/bank-detail-changes${status ? `?status=${status}` : ''}`)
export const approveBankChange = (id: string, callbackVerificationRef: string, comment: string) =>
  post<BankChangeDto>(`/bank-detail-changes/${id}/approve`, { callbackVerificationRef: callbackVerificationRef || null, comment: comment || null })
export const rejectBankChange = (id: string, comment: string) => post<BankChangeDto>(`/bank-detail-changes/${id}/reject`, { comment })

export const listEntities = () => get<LegalEntityDto[]>('/admin/legal-entities')
export const createEntity = (body: { code: string; legalName: string; countryCode: string; baseCurrency: string }) =>
  post<LegalEntityDto>('/admin/legal-entities', body)
export const setEntityActive = (id: string, active: boolean) => post<LegalEntityDto>(`/admin/legal-entities/${id}/${active ? 'activate' : 'deactivate'}`)
export const listUsers = () => get<UserDto[]>('/admin/users')
export const setUserActive = (id: string, active: boolean) => post<UserDto>(`/admin/users/${id}/${active ? 'enable' : 'disable'}`)
export const listRoles = () => get<RoleDto[]>('/admin/roles')
export const assignRole = (userId: string, roleId: string) => post<unknown>('/admin/role-assignments', { userId, roleId, legalEntityId: null })
export const listProviders = () => get<ProviderDto[]>('/admin/identity-providers')
export const setProviderEnabled = (id: string, enabled: boolean) => post<ProviderDto>(`/admin/identity-providers/${id}/${enabled ? 'enable' : 'disable'}`)
export const listAuthorities = () => get<AuthorityDto[]>('/admin/approval-authorities')
export const revokeAuthority = (id: string) => api<void>(`${V1}/admin/approval-authorities/${id}`, { method: 'DELETE' })
