import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { json, mockFetch, problem, renderApp, sentBody, staffSession } from '../test/helpers'

beforeEach(() => {
  document.documentElement.dir = 'ltr'
})
afterEach(() => vi.unstubAllGlobals())

const payable = (extra: Record<string, unknown> = {}) => ({
  invoiceId: 'i1', legalEntityId: 'e1', vendorId: 'v1', vendorName: 'Al Amal Trading', invoiceNumber: 'INV-100', invoiceDate: '2026-10-01', dueDate: '2026-11-01', currencyCode: 'SAR',
  total: 115, approvedAt: '2026-10-05T08:00:00Z', hasOpenRequest: false, blockers: [], ...extra,
})

const request = (extra: Record<string, unknown> = {}) => ({
  id: 'r1', legalEntityId: 'e1', vendorId: 'v1', vendorName: 'Al Amal Trading', invoiceId: 'i1', invoiceNumber: 'INV-100', invoiceVersion: 1, amount: 115, currencyCode: 'SAR',
  dueDate: '2026-11-01', status: 'Created', statusReason: null, proposalId: null, createdAt: '2026-10-06T08:00:00Z', ...extra,
})

const proposal = (status: string, extra: Record<string, unknown> = {}) => ({
  id: 'p1', legalEntityId: 'e1', proposalNo: 'PP-2026-000001', vendorId: 'v1', vendorName: 'Al Amal Trading', dueDate: '2026-11-01', currencyCode: 'SAR', totalAmount: 115, status,
  statusReason: null, createdBy: 'u1', createdAt: '2026-10-06T09:00:00Z', submittedBy: null, approvedBy: null, approvedAt: null, approvalComment: null, releasedBy: null, releasedAt: null,
  items: [{ paymentRequestId: 'r1', invoiceId: 'i1', invoiceNumber: 'INV-100', amount: 115 }], ...extra,
})

describe('payments page', () => {
  const routes = (extra: Record<string, (init?: RequestInit) => Response> = {}) => ({
    '/api/v1/session': () => json(staffSession),
    'GET /api/v1/payments/payable': () => json([payable(), payable({ invoiceId: 'i2', invoiceNumber: 'INV-200', blockers: ['no_verified_bank_details'] })]),
    'GET /api/v1/payments/requests': () => json([request()]),
    'GET /api/v1/payments/proposals': () => json([proposal('Draft')]),
    ...extra,
  })

  it('lists payable invoices, explains why one cannot be requested yet, and creates requests for the ticked ones', async () => {
    const fetchMock = mockFetch(routes({ 'POST /api/v1/payments/requests': () => json([{ invoiceId: 'i1', request: request(), errorCode: null }]) }))
    renderApp('en', '/payments')
    expect(await screen.findByText('No verified bank details.')).toBeInTheDocument()
    const blocked = screen.getByRole('link', { name: 'INV-200' }).closest('li')!
    expect(within(blocked).getByRole('checkbox')).toBeDisabled()

    await userEvent.click(within(screen.getByRole('link', { name: 'INV-100' }).closest('li')!).getByRole('checkbox'))
    await userEvent.click(screen.getByRole('button', { name: 'Create payment requests (1)' }))
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/api/v1/payments/requests')).toEqual({ invoiceIds: ['i1'] }))
    expect(await screen.findByText('1 payment request(s) created.')).toBeInTheDocument()
  })

  it('shows per-invoice failures from the server with their translated reason', async () => {
    mockFetch(routes({ 'POST /api/v1/payments/requests': () => json([{ invoiceId: 'i1', request: null, errorCode: 'request_exists' }]) }))
    renderApp('en', '/payments')
    await userEvent.click(within((await screen.findByRole('link', { name: 'INV-100' })).closest('li')!).getByRole('checkbox'))
    await userEvent.click(screen.getByRole('button', { name: 'Create payment requests (1)' }))
    expect(await screen.findByText('This invoice already has a payment request.')).toBeInTheDocument()
  })

  it('cancels a request only with a reason, groups ready requests, and links to proposals', async () => {
    const fetchMock = mockFetch(routes({
      'POST /api/v1/payments/requests/r1/cancel': () => json(request({ status: 'Cancelled' })),
      'POST /api/v1/payments/proposals': () => json([proposal('Draft')]),
    }))
    renderApp('en', '/payments')
    const cancel = await screen.findByRole('button', { name: 'Cancel request' })
    expect(cancel).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Reason for cancelling a request'), 'paid another way')
    await userEvent.click(cancel)
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/requests/r1/cancel')).toEqual({ reason: 'paid another way' }))

    await userEvent.click(screen.getByRole('button', { name: 'Group ready requests into proposals' }))
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/api/v1/payments/proposals')).toMatchObject({ legalEntityId: 'e1', requestIds: null }))
    expect(screen.getByRole('link', { name: 'PP-2026-000001' })).toHaveAttribute('href', '/payments/proposals/p1')
  })

  it('renders right-to-left in Arabic with the no-transfer notice', async () => {
    mockFetch(routes())
    renderApp('ar', '/payments')
    expect(await screen.findByText('يجهّز هذا النظام التعليمات فقط ولا يرسل أموالًا إلى أي بنك.')).toBeInTheDocument()
    expect(document.documentElement.dir).toBe('rtl')
  })
})

describe('proposal detail', () => {
  const routes = (status: string, extra: Record<string, (init?: RequestInit) => Response> = {}, recheck = { passed: true, invalidates: false, findings: [] as unknown[] }) => ({
    '/api/v1/session': () => json(staffSession),
    'GET /api/v1/payments/proposals/p1/recheck': () => json(recheck),
    'GET /api/v1/payments/proposals/p1/authorizations': () => json([]),
    'GET /api/v1/payments/proposals/p1/instruction': () => json({
      id: 'in1', proposalId: 'p1', proposalNo: 'PP-2026-000001', format: 'CSV+JSON v1', hash: 'abcdef0123456789abcdef', signature: 's', itemCount: 1, totalAmount: 115, currencyCode: 'SAR',
      status: 'Prepared', preparedBy: 'u3', preparedAt: '2026-10-07T08:00:00Z', handedOffBy: null, handedOffAt: null,
    }),
    'GET /api/v1/payments/proposals/p1': () => json(proposal(status)),
    ...extra,
  })

  it('walks a draft: shows the passing checks and submits for approval', async () => {
    const fetchMock = mockFetch(routes('Draft', { 'POST /api/v1/payments/proposals/p1/submit': () => json(proposal('PendingApproval')) }))
    renderApp('en', '/payments/proposals/p1')
    expect(await screen.findByRole('heading', { name: 'PP-2026-000001' })).toBeInTheDocument()
    expect(await screen.findByText(/All checks pass/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Submit for approval' }))
    await waitFor(() => expect(fetchMock.mock.calls.some(([u, i]) => String(u).endsWith('/p1/submit') && (i as RequestInit).method === 'POST')).toBe(true))
  })

  it('shows what would invalidate or hold back the proposal', async () => {
    mockFetch(routes('Approved', {}, {
      passed: false, invalidates: true,
      findings: [{ paymentRequestId: 'r1', invoiceId: 'i1', code: 'bank_details_changed', severity: 'Invalidates' }, { paymentRequestId: 'r1', invoiceId: 'i1', code: 'bank_change_pending', severity: 'Blocks' }],
    }))
    renderApp('en', '/payments/proposals/p1')
    expect(await screen.findByText(/The vendor's bank details changed\./)).toHaveTextContent('invalidates the proposal')
    expect(screen.getByText(/waiting for a decision/)).toHaveTextContent('holds it back for now')
  })

  it('approves with a comment, and sending back or cancelling needs one', async () => {
    const fetchMock = mockFetch(routes('PendingApproval', { 'POST /api/v1/payments/proposals/p1/approve': () => json(proposal('Approved')) }))
    renderApp('en', '/payments/proposals/p1')
    expect(await screen.findByRole('button', { name: 'Send back' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Cancel proposal' })).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Comment or reason'), 'looks right')
    await userEvent.click(screen.getByRole('button', { name: 'Approve' }))
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/p1/approve')).toEqual({ comment: 'looks right' }))
  })

  it('explains a refused release with the stable error code', async () => {
    mockFetch(routes('Approved', { 'POST /api/v1/payments/proposals/p1/release': () => problem(403, 'sod_violation') }))
    renderApp('en', '/payments/proposals/p1')
    await userEvent.click(await screen.findByRole('button', { name: 'Release and prepare the instruction' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('You cannot do this because you took part in the same item.')
  })

  it('shows the instruction after release with download links, and checks its signature', async () => {
    mockFetch(routes('Released', { 'GET /api/v1/payments/instructions/in1/verify': () => json({ hashMatches: true, signatureMatches: true, valid: true }) }))
    renderApp('en', '/payments/proposals/p1')
    expect(await screen.findByText(/Prepared and signed/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Download CSV' })).toHaveAttribute('href', '/api/v1/payments/instructions/in1/download?kind=csv')
    expect(screen.getByRole('link', { name: 'Download JSON' })).toHaveAttribute('href', '/api/v1/payments/instructions/in1/download?kind=json')
    expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Check signature' }))
    expect(await screen.findByText('The instruction matches its signature.')).toBeInTheDocument()
  })

  it('warns when the instruction does not match its signature', async () => {
    mockFetch(routes('Released', { 'GET /api/v1/payments/instructions/in1/verify': () => json({ hashMatches: false, signatureMatches: false, valid: false }) }))
    renderApp('en', '/payments/proposals/p1')
    await userEvent.click(await screen.findByRole('button', { name: 'Check signature' }))
    expect(await screen.findByText(/does NOT match its signature/)).toBeInTheDocument()
  })

  it('lists refused release attempts with the reasons', async () => {
    mockFetch(routes('Approved', {
      'GET /api/v1/payments/proposals/p1/authorizations': () => json([{
        id: 'a1', authorizedBy: 'u2', authorizedAt: '2026-10-07T08:00:00Z', decision: 'Denied', denialReason: 'separation', recheck: [], separation: [{ code: 'releaser_is_approver' }],
      }]),
    }))
    renderApp('en', '/payments/proposals/p1')
    const item = (await screen.findByText(/Refused/)).closest('li')!
    expect(item).toHaveTextContent('The releaser approved the proposal.')
  })
})
