import { fireEvent, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { json, mockFetch, renderApp, sentBody, staffSession } from '../test/helpers'

beforeEach(() => {
  document.documentElement.dir = 'ltr'
})
afterEach(() => vi.unstubAllGlobals())

const defaults = { FinancialRecords: 84, VendorData: 84, Audit: 120, Notifications: 6, WorkflowNotices: 6 }
const policy = (status: string, months: Record<string, number> = {}) => ({
  id: 'p1', versionNo: 2, status, settings: { months, countries: {} }, note: 'Shorter notices', createdBy: 'u9', createdAt: '2026-10-01T08:00:00Z', decidedBy: null, decidedAt: null, decisionComment: null,
})
const report = [
  { dataClass: 'FinancialRecords', months: 84, cutoff: '2019-10-01T00:00:00Z', disposedAutomatically: false, eligibleCount: 12, onHold: false },
  { dataClass: 'Audit', months: 120, cutoff: '2016-10-01T00:00:00Z', disposedAutomatically: false, eligibleCount: null, onHold: false },
  { dataClass: 'Notifications', months: 6, cutoff: '2026-04-01T00:00:00Z', disposedAutomatically: true, eligibleCount: 340, onHold: true },
]

describe('Data retention', () => {
  const routes = (extra: Record<string, (init?: RequestInit) => Response> = {}) => ({
    '/api/v1/session': () => json(staffSession),
    '/api/v1/notifications/unread-count': () => json({ count: 0 }),
    'GET /api/v1/admin/retention/report': () => json(report),
    'GET /api/v1/admin/retention': () => json({ active: null, pending: null, defaults, disposedAutomatically: ['Notifications', 'WorkflowNotices'] }),
    'GET /api/v1/admin/legal-holds': () => json([]),
    'GET /api/v1/admin/legal-entities': () => json([]),
    ...extra,
  })

  it('shows the standard periods, which kinds are deleted automatically, and the counts past retention', async () => {
    mockFetch(routes())
    renderApp('en', '/admin/retention')
    expect(await screen.findByText('No policy has been set; the standard periods apply.')).toBeInTheDocument()
    expect(await screen.findAllByText('Deleted automatically')).toHaveLength(2)
    expect(screen.getAllByText('Reported only, never deleted')).toHaveLength(3)
    expect(await screen.findByText('340')).toBeInTheDocument()
    expect(screen.getByText('Not counted')).toBeInTheDocument()
    expect(screen.getByText('On hold')).toBeInTheDocument()
  })

  it('proposes a change only with a reason and sends every kind with its months', async () => {
    const fetchMock = mockFetch(routes({ 'PUT /api/v1/admin/retention': () => json(policy('Pending', { Notifications: 12 })) }))
    renderApp('en', '/admin/retention')
    const months = await screen.findByLabelText('Notifications — Keep for (months)')
    fireEvent.change(months, { target: { value: '12' } })
    const propose = screen.getByRole('button', { name: 'Propose change' })
    expect(propose).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Why is this changing?'), 'Keep a year')
    await userEvent.click(propose)
    await waitFor(() => expect(sentBody(fetchMock, 'PUT', '/api/v1/admin/retention')).toMatchObject({
      note: 'Keep a year', settings: { months: { Notifications: 12, FinancialRecords: 84, Audit: 120, VendorData: 84, WorkflowNotices: 6 } },
    }))
  })

  it('shows a waiting change with approve, reject and cancel, and blocks a second proposal', async () => {
    mockFetch(routes({ 'GET /api/v1/admin/retention': () => json({ active: null, pending: policy('Pending', { Notifications: 12 }), defaults, disposedAutomatically: [] }) }))
    renderApp('en', '/admin/retention')
    expect(await screen.findByText('Waiting for approval: version 2')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Approve' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reject' })).toBeDisabled() // a reason is needed
    expect(screen.getByRole('button', { name: 'Propose change' })).toBeDisabled()
  })

  it('places a hold only with a reason', async () => {
    const fetchMock = mockFetch(routes({ 'POST /api/v1/admin/legal-holds': () => json({ id: 'h1', scope: 'Tenant', reason: 'Regulator', placedBy: 'u1', placedAt: '2026-10-01T08:00:00Z' }) }))
    renderApp('en', '/admin/retention')
    const place = await screen.findByRole('button', { name: 'Place hold' })
    expect(place).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Reason'), 'Regulator enquiry')
    await userEvent.click(place)
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/api/v1/admin/legal-holds')).toMatchObject({ scope: 'Tenant', reason: 'Regulator enquiry' }))
  })

  it('renders in Arabic, right to left', async () => {
    mockFetch(routes())
    renderApp('ar', '/admin/retention')
    expect(await screen.findByRole('heading', { name: 'الاحتفاظ بالبيانات', level: 1 })).toBeInTheDocument()
    expect(document.documentElement.dir).toBe('rtl')
  })
})
