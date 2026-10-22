import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { json, mockFetch, problem, renderApp, sentBody, staffSession } from '../test/helpers'

beforeEach(() => {
  document.documentElement.dir = 'ltr'
})
afterEach(() => vi.unstubAllGlobals())

const entity = { id: 'e1', code: 'KSA', legalName: 'Acme KSA', countryCode: 'SA', baseCurrency: 'SAR', status: 'Active' }

const item = (extra: Record<string, unknown> = {}) => ({
  taskId: 't1', runId: 'r1', stepId: 'l1', subjectType: 'Invoice', subjectId: 'i1', subjectLabel: 'INV-100 - Al Amal Trading', legalEntityId: 'e1', amount: 115,
  currency: 'SAR', mode: 'AnyOne', approvalsSoFar: 0, approvers: 1, createdAt: '2026-10-02T08:00:00Z', dueAt: '2026-10-04T08:00:00Z', overdue: false, escalated: false,
  actingForId: null, actingForName: null, ...extra,
})

const run = (status = 'Active', extra: Record<string, unknown> = {}) => ({
  id: 'r1', subjectType: 'Invoice', subjectId: 'i1', subjectVersion: 1, subjectLabel: 'INV-100 - Al Amal Trading', legalEntityId: 'e1', status, statusReason: null,
  definitionVersion: 2, startedAt: '2026-10-02T08:00:00Z', completedAt: null, amount: 115, currency: 'SAR', needsAttention: false,
  tasks: [{
    id: 't1', stepId: 'l1', branchPath: '', status: status === 'Active' ? 'Pending' : 'Completed', mode: 'AnyOne', quorum: 0, assignees: ['u2'], assigneeNames: ['Bob'],
    createdAt: '2026-10-02T08:00:00Z', dueAt: null, completedAt: null, decisions: [], escalatedAt: null, escalatedTo: null,
  }], ...extra,
})

describe('approver inbox', () => {
  it('lists waiting approvals with deadline, escalation and delegation hints', async () => {
    mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/approvals/inbox': () => json([item({ overdue: true, escalated: true }), item({ taskId: 't2', subjectLabel: 'INV-200 - Other', actingForId: 'u9', actingForName: 'Dana', dueAt: null })]),
      'GET /api/v1/approvals/notices': () => json([{ id: 'n1', kind: 'Reminder', runId: 'r1', taskId: 't1', subjectLabel: 'INV-100 - Al Amal Trading', createdAt: '2026-10-03T08:00:00Z', readAt: null }]),
    })
    renderApp('en', '/approvals')
    expect(await screen.findByRole('link', { name: 'INV-100 - Al Amal Trading' })).toHaveAttribute('href', '/approvals/t1')
    expect(screen.getByText(/Overdue/)).toBeInTheDocument()
    expect(screen.getByText('You can decide this for Dana.')).toBeInTheDocument()
    expect(screen.getByText('Reminder: INV-100 - Al Amal Trading is waiting for your decision.')).toBeInTheDocument()
  })

  it('shows an empty state, and renders right-to-left in Arabic', async () => {
    mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/approvals/inbox': () => json([]),
      'GET /api/v1/approvals/notices': () => json([]),
    })
    renderApp('ar', '/approvals')
    expect(await screen.findByText('لا شيء بانتظارك.')).toBeInTheDocument()
    expect(document.documentElement.dir).toBe('rtl')
  })

  it('marks a notice as read', async () => {
    const fetchMock = mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/approvals/inbox': () => json([]),
      'GET /api/v1/approvals/notices': () => json([{ id: 'n1', kind: 'Escalated', runId: 'r1', taskId: 't1', subjectLabel: 'INV-9', createdAt: '2026-10-03T08:00:00Z', readAt: null }]),
      'POST /api/v1/approvals/notices/n1/read': () => new Response(null, { status: 204 }),
    })
    renderApp('en', '/approvals')
    await userEvent.click(await screen.findByRole('button', { name: 'Mark as read' }))
    await waitFor(() => expect(fetchMock.mock.calls.some(([u, i]) => String(u).endsWith('/notices/n1/read') && (i as RequestInit).method === 'POST')).toBe(true))
  })
})

describe('approval task', () => {
  const routes = (decision?: () => Response, inbox = [item()]) => ({
    '/api/v1/session': () => json(staffSession),
    'GET /api/v1/approvals/tasks/t1': () => json(run()),
    'GET /api/v1/approvals/inbox': () => json(inbox),
    ...(decision ? { 'POST /api/v1/approvals/tasks/t1/decision': decision } : {}),
  })

  it('approves without a comment, and requires one to return or reject', async () => {
    const fetchMock = mockFetch(routes(() => json(run('Approved'))))
    renderApp('en', '/approvals/t1')
    expect(await screen.findByRole('heading', { name: 'INV-100 - Al Amal Trading' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reject' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Return for correction' })).toBeDisabled()

    await userEvent.click(screen.getByRole('button', { name: 'Approve' }))
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/decision')).toMatchObject({ decision: 'Approve', comment: null, actingFor: null }))
  })

  it('sends the comment when rejecting and acts for the delegator when delegated', async () => {
    const fetchMock = mockFetch(routes(() => json(run('Rejected')), [item({ actingForId: 'u9', actingForName: 'Dana' })]))
    renderApp('en', '/approvals/t1')
    await userEvent.type(await screen.findByLabelText('Comment'), 'Wrong vendor')
    await userEvent.click(screen.getByRole('button', { name: 'Reject' }))
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/decision')).toMatchObject({ decision: 'Reject', comment: 'Wrong vendor', actingFor: 'u9' }))
  })

  it('explains a refused decision with the stable error code, not the server text', async () => {
    mockFetch(routes(() => problem(403, 'approval_limit_exceeded')))
    renderApp('en', '/approvals/t1')
    await userEvent.click(await screen.findByRole('button', { name: 'Approve' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('The amount is above the approval authority of this approver.')
  })

  it('shows the history with decisions made for someone else, and no form once the step is closed', async () => {
    const done = run('Approved')
    done.tasks[0].decisions = [{
      id: 'd1', actorId: 'u3', actorName: 'Carol', actingForId: 'u2', actingForName: 'Bob', decision: 'Approve', comment: 'covering', decidedAt: '2026-10-03T09:00:00Z',
    }] as never
    mockFetch({ ...routes(), 'GET /api/v1/approvals/tasks/t1': () => json(done), 'GET /api/v1/approvals/inbox': () => json([]) })
    renderApp('en', '/approvals/t1')
    expect(await screen.findByText(/Carol/)).toHaveTextContent('on behalf of Bob')
    expect(screen.getByText(/covering/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('no longer waiting')
  })
})

describe('invoice detail approval actions', () => {
  const invoice = (status: string) => ({
    id: 'i1', legalEntityId: 'e1', vendorId: 'v1', vendorName: 'Al Amal Trading', invoiceNumber: 'INV-100', invoiceDate: '2026-10-01', dueDate: null, currencyCode: 'SAR',
    subtotal: 100, taxTotal: 15, total: 115, poReference: null, supplierTaxInvoiceRef: null, supplierTaxNumber: null, source: 'Manual', status, version: 1,
    createdAt: '2026-10-02T08:00:00Z', lines: [], attachments: [],
  })
  const base = (status: string, extra: Record<string, () => Response> = {}) => ({
    '/api/v1/session': () => json(staffSession),
    'GET /api/v1/invoices/i1/verification': () => json({ invoiceId: 'i1', invoiceStatus: status, invoiceVersion: 1, ruleSetVersion: 0, results: [], exceptions: [] }),
    'GET /api/v1/invoices/i1/extractions': () => json([]),
    'GET /api/v1/invoices/i1/approvals': () => json([run()]),
    'GET /api/v1/invoices/i1': () => json(invoice(status)),
    ...extra,
  })

  it('sends a verified invoice for approval', async () => {
    const fetchMock = mockFetch(base('Verified', { 'POST /api/v1/invoices/i1/send-for-approval': () => json(invoice('InApproval')) }))
    renderApp('en', '/invoices/i1')
    await userEvent.click(await screen.findByRole('button', { name: 'Send for approval' }))
    await waitFor(() => expect(fetchMock.mock.calls.some(([u, i]) => String(u).endsWith('/send-for-approval') && (i as RequestInit).method === 'POST')).toBe(true))
  })

  it('shows the approval history, and offers a change instead of an edit while in approval', async () => {
    mockFetch(base('InApproval'))
    renderApp('en', '/invoices/i1')
    expect(await screen.findByRole('heading', { name: 'Approval history' })).toBeInTheDocument()
    expect(screen.getByText(/In progress \(workflow version 2\)/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Change this invoice' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Send for approval' })).not.toBeInTheDocument()
  })
})

describe('delegations', () => {
  it('creates a delegation for the chosen approver and period', async () => {
    const fetchMock = mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/admin/legal-entities': () => json([entity]),
      'GET /api/v1/approvals/approvers': () => json([{ id: 'u2', name: 'Bob' }]),
      'GET /api/v1/delegations': () => json([]),
      'POST /api/v1/delegations': () => json({ id: 'd1' }),
    })
    renderApp('en', '/delegations')
    await screen.findByRole('option', { name: 'Bob' })
    await userEvent.selectOptions(screen.getByLabelText('Delegate'), 'u2')
    await userEvent.type(screen.getByLabelText('Until'), '2026-12-31')
    await userEvent.type(screen.getByLabelText('Reason'), 'Annual leave')
    await userEvent.click(screen.getByRole('button', { name: 'Create delegation' }))
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/api/v1/delegations')).toMatchObject({ legalEntityId: 'e1', delegateId: 'u2', maxAmount: null, reason: 'Annual leave' }))
  })

  it('lists delegations with their state and lets the owner revoke an active one', async () => {
    const fetchMock = mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/admin/legal-entities': () => json([entity]),
      'GET /api/v1/approvals/approvers': () => json([]),
      'GET /api/v1/delegations': () => json([
        { id: 'd1', legalEntityId: 'e1', delegatorId: 'u1', delegatorName: 'Alice', delegateId: 'u2', delegateName: 'Bob', validFrom: '2026-10-01T00:00:00Z', validTo: '2026-10-08T00:00:00Z', maxAmount: 5000, status: 'Active', reason: 'Leave' },
        { id: 'd2', legalEntityId: 'e1', delegatorId: 'u1', delegatorName: 'Alice', delegateId: 'u3', delegateName: 'Carol', validFrom: '2026-09-01T00:00:00Z', validTo: '2026-09-08T00:00:00Z', maxAmount: null, status: 'Expired', reason: 'Old' },
      ]),
      'POST /api/v1/delegations/d1/revoke': () => json({ id: 'd1' }),
    })
    renderApp('en', '/delegations')
    const active = (await screen.findByText(/Bob/)).closest('li')!
    expect(within(active).getByText(/Active/)).toBeInTheDocument()
    expect(within(active).getByText(/up to 5000/)).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Revoke' })).toHaveLength(1) // the expired one cannot be revoked
    await userEvent.click(within(active).getByRole('button', { name: 'Revoke' }))
    await waitFor(() => expect(fetchMock.mock.calls.some(([u]) => String(u).endsWith('/delegations/d1/revoke'))).toBe(true))
  })
})

describe('workflow administration', () => {
  const version = (extra: Record<string, unknown> = {}) => ({
    id: 'w1', legalEntityId: 'e1', versionNo: 3, status: 'Active', source: 'Authored', note: 'n', createdBy: 'u1', createdAt: '2026-10-01T00:00:00Z',
    spec: { name: 'Two levels', start: 'l1', steps: [{ id: 'l1', type: 'Approval', next: 'l2', approvers: [{ kind: 'Role', value: 'Finance Manager' }], mode: 'AnyOne', dueHours: 48 }, { id: 'l2', type: 'Approval', approvers: [{ kind: 'Role', value: 'CFO' }], mode: 'All' }] },
    ...extra,
  })
  const common = (extra: Record<string, (init?: RequestInit) => Response> = {}) => ({
    '/api/v1/session': () => json(staffSession),
    'GET /api/v1/admin/legal-entities/e1/workflow': () => json({ active: version(), pending: null }),
    'GET /api/v1/admin/legal-entities/e1/approval-materiality': () => json({ active: { id: null, versionNo: 0, status: 'Default', config: { amountPercent: 0, amountAbsolute: 0, materialFields: ['total'] }, note: '' }, pending: null }),
    'GET /api/v1/approval-materiality/fields': () => json({ money: ['total', 'taxTotal'], other: ['currency'] }),
    'GET /api/v1/approval-materiality-changes': () => json([]),
    'GET /api/v1/workflow-changes': () => json([]),
    'GET /api/v1/admin/legal-entities': () => json([entity]),
    ...extra,
  })

  it('shows the workflow in force as readable steps', async () => {
    mockFetch(common())
    renderApp('en', '/workflow')
    expect(await screen.findByText('Version 3 (written directly)')).toBeInTheDocument()
    expect(screen.getAllByText(/Finance Manager/).length).toBeGreaterThan(0)
    expect(screen.getByText(/due after 48 h/)).toBeInTheDocument()
  })

  it('reports validation problems in the editor and proposes a valid definition with a reason', async () => {
    const fetchMock = mockFetch(common({
      'POST /api/v1/admin/legal-entities/e1/workflow/validate': () => json([{ code: 'cycle', message: 'x', stepId: null }]),
      'PUT /api/v1/admin/legal-entities/e1/workflow': () => json(version({ id: 'w2', versionNo: 4, status: 'Pending' })),
    }))
    renderApp('en', '/workflow')
    await screen.findByText('Version 3 (written directly)')
    await userEvent.click(screen.getByRole('button', { name: 'Check' }))
    expect(await screen.findByText('A workflow must not loop back to an earlier step.')).toBeInTheDocument()

    await userEvent.type(screen.getAllByLabelText('Why is it changing?')[0], 'Add CFO level')
    await userEvent.click(screen.getByRole('button', { name: 'Propose this workflow' }))
    await waitFor(() => expect(sentBody(fetchMock, 'PUT', '/workflow')).toMatchObject({ fromMatrix: false, note: 'Add CFO level', spec: { name: 'Two levels' } }))
    expect(await screen.findByText('Saved. It will be used once another person approves it.')).toBeInTheDocument()
  })

  it('proposes the approval matrix without a hand-written definition', async () => {
    const fetchMock = mockFetch(common({ 'PUT /api/v1/admin/legal-entities/e1/workflow': () => json(version({ status: 'Pending' })) }))
    renderApp('en', '/workflow')
    await screen.findByText('Version 3 (written directly)')
    await userEvent.type(screen.getAllByLabelText('Why is it changing?')[0], 'Use the matrix')
    await userEvent.click(screen.getByRole('button', { name: 'Propose the approval matrix' }))
    await waitFor(() => expect(sentBody(fetchMock, 'PUT', '/workflow')).toMatchObject({ fromMatrix: true, spec: null }))
  })

  it('shows the route a simulated invoice would take', async () => {
    mockFetch(common({
      'POST /api/v1/admin/legal-entities/e1/workflow/simulate': () => json({ outcome: 'Approved', route: [{ stepId: 'l1', approvers: ['a', 'b'], mode: 'AnyOne' }], issues: [], problem: null }),
    }))
    renderApp('en', '/workflow')
    await screen.findByText('Version 3 (written directly)')
    await userEvent.click(screen.getByRole('button', { name: 'Show the route' }))
    expect(await screen.findByText(/2 approvers/)).toBeInTheDocument()
  })

  it('lets another person approve or reject a pending workflow change', async () => {
    const fetchMock = mockFetch(common({
      'GET /api/v1/workflow-changes': () => json([{ change: version({ id: 'w9', status: 'Pending', versionNo: 4, note: 'Add CFO' }), base: version() }]),
      'POST /api/v1/workflow-changes/w9/approve': () => json(version({ id: 'w9' })),
    }))
    renderApp('en', '/workflow')
    expect(await screen.findByText('Add CFO')).toBeInTheDocument()
    expect(screen.getByText('Replaces version 3')).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Reject' })[0]).toBeDisabled() // a reason is needed
    await userEvent.click(screen.getByRole('button', { name: 'Approve' }))
    await waitFor(() => expect(fetchMock.mock.calls.some(([u]) => String(u).endsWith('/workflow-changes/w9/approve'))).toBe(true))
  })

  it('proposes materiality settings', async () => {
    const fetchMock = mockFetch(common({ 'PUT /api/v1/admin/legal-entities/e1/approval-materiality': () => json({ id: 'm1', versionNo: 1, status: 'Pending' }) }))
    renderApp('en', '/workflow')
    const heading = await screen.findByRole('heading', { name: 'When a change needs approval again' })
    const section = heading.closest('section')!
    await userEvent.clear(within(section).getByLabelText('Allowed change in amounts (%)'))
    await userEvent.type(within(section).getByLabelText('Allowed change in amounts (%)'), '2')
    await userEvent.type(within(section).getByLabelText('Why is it changing?'), 'Rounding differences')
    await userEvent.click(within(section).getByRole('button', { name: 'Propose these settings' }))
    await waitFor(() => expect(sentBody(fetchMock, 'PUT', '/approval-materiality')).toMatchObject({ config: { amountPercent: 2 }, note: 'Rounding differences' }))
  })
})
