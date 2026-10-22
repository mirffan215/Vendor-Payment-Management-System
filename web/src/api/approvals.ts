import { api } from './client'

const V1 = '/api/v1'
const json = (body: unknown): RequestInit => ({ headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
const post = <T>(path: string, body: unknown = {}) => api<T>(`${V1}${path}`, { method: 'POST', ...json(body) })
const put = <T>(path: string, body: unknown) => api<T>(`${V1}${path}`, { method: 'PUT', ...json(body) })
const get = <T>(path: string) => api<T>(`${V1}${path}`)

// --- approver side ---------------------------------------------------------------------------------------

export interface InboxItem {
  taskId: string
  runId: string
  stepId: string
  subjectType: string
  subjectId: string
  subjectLabel: string
  legalEntityId: string
  amount: number
  currency: string
  mode: string
  approvalsSoFar: number
  approvers: number
  createdAt: string
  dueAt: string | null
  overdue: boolean
  escalated: boolean
  actingForId: string | null
  actingForName: string | null
}

export interface ApprovalDecision {
  id: string
  actorId: string
  actorName: string | null
  actingForId: string | null
  actingForName: string | null
  decision: 'Approve' | 'Reject' | 'Return'
  comment: string | null
  decidedAt: string
}

export interface ApprovalTask {
  id: string
  stepId: string
  branchPath: string
  status: string
  mode: string
  quorum: number
  assignees: string[]
  assigneeNames: (string | null)[]
  createdAt: string
  dueAt: string | null
  completedAt: string | null
  decisions: ApprovalDecision[]
  escalatedAt: string | null
  escalatedTo: string[] | null
}

export interface ApprovalRun {
  id: string
  subjectType: string
  subjectId: string
  subjectVersion: number
  subjectLabel: string
  legalEntityId: string
  status: string
  statusReason: string | null
  definitionVersion: number
  startedAt: string
  completedAt: string | null
  amount: number
  currency: string
  needsAttention: boolean
  tasks: ApprovalTask[]
}

export const getInbox = () => get<InboxItem[]>('/approvals/inbox')
export const getApprovalTask = (taskId: string) => get<ApprovalRun>(`/approvals/tasks/${taskId}`)
export const decideTask = (taskId: string, decision: string, comment: string, actingFor: string | null) =>
  post<ApprovalRun>(`/approvals/tasks/${taskId}/decision`, { decision, comment: comment || null, actingFor })
export const getInvoiceApprovals = (invoiceId: string) => get<ApprovalRun[]>(`/invoices/${invoiceId}/approvals`)
export const sendForApproval = (invoiceId: string) => post<unknown>(`/invoices/${invoiceId}/send-for-approval`)
export const reviseInvoice = (invoiceId: string, data: unknown, reason: string) =>
  post<{ material: boolean; changedFields: string[] }>(`/invoices/${invoiceId}/revise`, { data, reason })

export interface Notice {
  id: string
  kind: 'Reminder' | 'Escalated' | 'EscalatedAway' | 'Stalled'
  runId: string
  taskId: string
  subjectLabel: string
  createdAt: string
  readAt: string | null
}
export const getNotices = () => get<Notice[]>('/approvals/notices')
export const markNoticeRead = (id: string) => api<void>(`${V1}/approvals/notices/${id}/read`, { method: 'POST', ...json({}) })

// --- delegation ------------------------------------------------------------------------------------------

export interface Delegation {
  id: string
  legalEntityId: string
  delegatorId: string
  delegatorName: string | null
  delegateId: string
  delegateName: string | null
  validFrom: string
  validTo: string
  maxAmount: number | null
  status: 'Active' | 'Revoked' | 'Expired'
  reason: string
}
export const getDelegations = (activeOnly = false) => get<Delegation[]>(`/delegations?activeOnly=${activeOnly}`)
export const createDelegation = (body: { legalEntityId: string; delegateId: string; validFrom: string; validTo: string; maxAmount: number | null; reason: string }) =>
  post<Delegation>('/delegations', body)
export const revokeDelegation = (id: string) => post<Delegation>(`/delegations/${id}/revoke`)

// --- administration --------------------------------------------------------------------------------------

export interface ClauseSpec { field: string; op: string; values: string[] }
export interface ApproverSpec { kind: 'User' | 'Role'; value: string }
export interface StepSpec {
  id: string
  type: 'Approval' | 'Parallel' | 'Condition'
  next?: string | null
  approvers?: ApproverSpec[]
  mode?: string
  quorum?: number
  dueHours?: number | null
  reminderHours?: number | null
  escalateTo?: ApproverSpec | null
  branches?: string[]
  join?: string
  cases?: { all: ClauseSpec[]; next: string | null }[]
  default?: string | null
}
export interface WorkflowSpec { name: string; start: string; steps: StepSpec[] }
export interface WorkflowVersion {
  id: string | null
  legalEntityId: string
  versionNo: number
  status: string
  source: string
  spec: WorkflowSpec | null
  note: string
  createdBy: string | null
  createdAt: string | null
}
export interface EntityWorkflow { active: WorkflowVersion | null; pending: WorkflowVersion | null }
export interface WorkflowChange { change: WorkflowVersion; base: WorkflowVersion | null }
export interface DefinitionIssue { code: string; message: string; stepId: string | null }
export interface Simulation {
  outcome: string
  route: { stepId: string; approvers: string[]; mode: string }[]
  issues: DefinitionIssue[]
  problem: string | null
}

export const getEntityWorkflow = (entityId: string) => get<EntityWorkflow>(`/admin/legal-entities/${entityId}/workflow`)
export const proposeWorkflow = (entityId: string, spec: WorkflowSpec | null, fromMatrix: boolean, note: string) =>
  put<WorkflowVersion>(`/admin/legal-entities/${entityId}/workflow`, { spec, fromMatrix, note })
export const validateWorkflow = (entityId: string, spec: WorkflowSpec) => post<DefinitionIssue[]>(`/admin/legal-entities/${entityId}/workflow/validate`, spec)
export const previewMatrix = (entityId: string) => get<WorkflowSpec>(`/admin/legal-entities/${entityId}/workflow/matrix-preview`)
export const simulateWorkflow = (entityId: string, body: { spec: WorkflowSpec | null; amount: number; currency: string; vendorId: string | null; source: string | null; dimensions: Record<string, string> | null; maker: string | null }) =>
  post<Simulation>(`/admin/legal-entities/${entityId}/workflow/simulate`, body)
export const listWorkflowChanges = (status: string) => get<WorkflowChange[]>(`/workflow-changes?status=${status}`)
export const decideWorkflowChange = (id: string, decision: 'approve' | 'reject', comment: string) =>
  post<WorkflowVersion>(`/workflow-changes/${id}/${decision}`, { comment })

export interface MaterialityConfig { amountPercent: number; amountAbsolute: number; materialFields: string[] }
export interface MaterialityVersion { id: string | null; versionNo: number; status: string; config: MaterialityConfig; note: string }
export interface EntityMateriality { active: MaterialityVersion; pending: MaterialityVersion | null }
export const getMateriality = (entityId: string) => get<EntityMateriality>(`/admin/legal-entities/${entityId}/approval-materiality`)
export const getMaterialityFields = () => get<{ money: string[]; other: string[] }>('/approval-materiality/fields')
export const proposeMateriality = (entityId: string, config: MaterialityConfig, note: string) =>
  put<MaterialityVersion>(`/admin/legal-entities/${entityId}/approval-materiality`, { config, note })
export const listMaterialityChanges = (status: string) => get<{ change: MaterialityVersion & { legalEntityId: string }; base: MaterialityVersion }[]>(`/approval-materiality-changes?status=${status}`)
export const decideMaterialityChange = (id: string, decision: 'approve' | 'reject', comment: string) =>
  post<MaterialityVersion>(`/approval-materiality-changes/${id}/${decision}`, { comment })

export interface ApproverOption { id: string; name: string }
export const getApprovers = (legalEntityId: string) => get<ApproverOption[]>(`/approvals/approvers?legalEntityId=${legalEntityId}`)
