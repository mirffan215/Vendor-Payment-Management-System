export interface Problem {
  status: number
  title?: string
  errorCode?: string
  traceId?: string
  correlationId?: string
}

export class ApiError extends Error {
  readonly status: number
  readonly problem: Problem

  constructor(problem: Problem) {
    super(problem.title ?? `HTTP ${problem.status}`)
    this.status = problem.status
    this.problem = problem
  }
}

let csrfToken: string | undefined

export function setCsrfToken(token: string | undefined): void {
  csrfToken = token
}

const UNSAFE = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])

/** Cookie-session API client: same-origin credentials, antiforgery header on writes, RFC 7807 errors. */
export async function api<T>(path: string, init: RequestInit = {}): Promise<T> {
  const method = (init.method ?? 'GET').toUpperCase()
  const headers = new Headers(init.headers)
  headers.set('Accept', 'application/json')
  if (UNSAFE.has(method) && csrfToken) headers.set('X-XSRF-TOKEN', csrfToken)

  const response = await fetch(path, { ...init, method, headers, credentials: 'same-origin' })
  if (!response.ok) {
    let problem: Problem = { status: response.status }
    try {
      problem = { ...(await response.json()), status: response.status }
    } catch {
      // body is not JSON; keep the status only
    }
    throw new ApiError(problem)
  }
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

export interface SessionInfo {
  userId: string
  tenantId: string
  email: string
  displayName: string
  locale: string
  /** Absent when the server predates saved preferences; then the browser's own choice stands. */
  calendar?: string
  csrfToken: string
}

export interface ProviderSummary {
  id: string
  displayName: string
  kind: number
}

export interface AuditEventDto {
  sequence: number
  id: string
  occurredAt: string
  actorType: string
  action: string
  entityType: string
  entityId?: string | null
  correlationId?: string | null
  hash: string
}

export interface AuditPageDto {
  items: AuditEventDto[]
  nextAfterSequence?: number | null
}

export const getSession = () => api<SessionInfo>('/api/v1/session')
export const savePreferences = (locale: string, calendar: string) =>
  api<{ locale: string; calendar: string }>('/api/v1/session/preferences', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ locale, calendar }),
  })
export const getProviders = (tenantSlug: string) =>
  api<ProviderSummary[]>(`/auth/providers?tenant=${encodeURIComponent(tenantSlug)}`)
export interface AuditFilters { action?: string; entityType?: string; from?: string; to?: string }

const auditQuery = (filters: AuditFilters) => {
  const p = new URLSearchParams()
  if (filters.action) p.set('action', filters.action)
  if (filters.entityType) p.set('entityType', filters.entityType)
  if (filters.from) p.set('from', `${filters.from}T00:00:00Z`)
  if (filters.to) p.set('to', `${filters.to}T23:59:59Z`)
  return p
}

export const getAuditEvents = (afterSequence?: number | null, filters: AuditFilters = {}) => {
  const p = auditQuery(filters)
  p.set('limit', '50')
  if (afterSequence) p.set('afterSequence', String(afterSequence))
  return api<AuditPageDto>(`/api/v1/audit/events?${p.toString()}`)
}

export const auditEvidenceUrl = (filters: AuditFilters) => `/api/v1/audit/evidence?${auditQuery(filters).toString()}`

export interface EvidenceVerification { isValid: boolean; eventsChecked: number; signatureValid: boolean; filesIntact: boolean; eventsIntact: boolean; chainLinked: boolean; problems: string[] }

export const verifyAuditEvidence = (file: File) => {
  const form = new FormData()
  form.append('file', file)
  return api<EvidenceVerification>('/api/v1/audit/evidence/verify', { method: 'POST', body: form })
}
export const signOut = () => api<void>('/auth/logout', { method: 'POST' })
