import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { json, mockFetch, problem, renderApp, sentBody, staffSession } from '../test/helpers'

beforeEach(() => {
  document.documentElement.dir = 'ltr'
})
afterEach(() => vi.unstubAllGlobals())

const entity = { id: 'e1', code: 'KSA', legalName: 'Acme KSA', countryCode: 'SA', baseCurrency: 'SAR', status: 'Active' }
const bell = { '/api/v1/notifications/unread-count': () => json({ count: 2 }) }

const aging = {
  key: 'payables-aging', title: 'Payables aging', locale: 'en', calendar: 'gregorian', generatedAt: '2026-10-05T08:00:00Z', filters: { asOf: '2026-10-05' }, truncated: false,
  tables: [
    {
      name: 'summary', title: 'Summary', columns: [{ key: 'currency', label: 'Currency', type: 'text' }, { key: 'total', label: 'Total', type: 'money' }],
      rows: [['SAR', 1150], ['USD', 200]],
    },
    { name: 'detail', title: 'Detail', columns: [{ key: 'n', label: 'Number', type: 'text' }], rows: [] },
  ],
}

describe('Reports', () => {
  const routes = (extra: Record<string, (init?: RequestInit) => Response> = {}) => ({
    '/api/v1/session': () => json(staffSession),
    ...bell,
    'GET /api/v1/reports/payables-aging': () => json(aging),
    'GET /api/v1/reports?': () => json([{ key: 'payables-aging', title: 'Payables aging', description: 'Open invoices by age.', needsPeriod: false, needsTaxPermission: false }]),
    'GET /api/v1/admin/legal-entities': () => json([entity]),
    ...extra,
  })

  it('runs a report, keeps currencies on separate rows and offers the three export formats', async () => {
    const fetchMock = mockFetch(routes())
    renderApp('en', '/reports')
    await userEvent.click(await screen.findByRole('button', { name: 'Run report' }))
    expect(await screen.findByRole('heading', { name: 'Summary' })).toBeInTheDocument()
    const rows = screen.getAllByRole('row').map((r) => r.textContent)
    expect(rows.some((r) => r?.includes('SAR') && r.includes('1,150.00'))).toBe(true)
    expect(rows.some((r) => r?.includes('USD') && r.includes('200.00'))).toBe(true)
    expect(screen.getByText('No rows for these filters.')).toBeInTheDocument()
    for (const name of ['CSV', 'Excel', 'PDF']) expect(screen.getByRole('link', { name })).toHaveAttribute('href', expect.stringContaining('/api/v1/reports/payables-aging/export?'))
    expect(fetchMock.mock.calls.some(([u]) => String(u).includes('/reports/payables-aging?') && String(u).includes('asOf='))).toBe(true)
  })

  it('shows a problem when the report cannot be run', async () => {
    mockFetch(routes({ 'GET /api/v1/reports/payables-aging': () => problem(403, 'forbidden') }))
    renderApp('en', '/reports')
    await userEvent.click(await screen.findByRole('button', { name: 'Run report' }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
  })

  it('renders in Arabic, right to left', async () => {
    mockFetch(routes())
    renderApp('ar', '/reports')
    expect(await screen.findByRole('heading', { name: 'التقارير', level: 1 })).toBeInTheDocument()
    expect(document.documentElement.dir).toBe('rtl')
    expect(await screen.findByRole('button', { name: 'تشغيل التقرير' })).toBeInTheDocument()
  })
})

describe('Notifications', () => {
  const item = (id: string, readAt: string | null) => ({ id, kind: 'ApprovalAssigned', title: `Approve INV-${id}`, body: 'Waiting for you', path: '/approvals', createdAt: '2026-10-05T08:00:00Z', readAt, mandatory: true })
  const prefs = [
    { kind: 'ApprovalAssigned', channel: 'Email', enabled: true, mandatory: true },
    { kind: 'PaymentReleased', channel: 'Email', enabled: true, mandatory: false },
  ]
  const routes = (extra: Record<string, (init?: RequestInit) => Response> = {}) => ({
    '/api/v1/session': () => json(staffSession),
    ...bell,
    'GET /api/v1/notifications/preferences': () => json(prefs),
    'GET /api/v1/notifications?': () => json([item('1', null), item('2', '2026-10-05T09:00:00Z')]),
    ...extra,
  })

  it('lists notifications, marks one read, and shows the unread count on the bell', async () => {
    const fetchMock = mockFetch(routes({ 'POST /api/v1/notifications/1/read': () => json({}) }))
    renderApp('en', '/notifications')
    expect(await screen.findByText('Approve INV-1')).toBeInTheDocument()
    expect(await screen.findByLabelText('Notifications, 2 unread')).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Mark as read' }))
    await waitFor(() => expect(fetchMock.mock.calls.some(([u, i]) => String(u).endsWith('/notifications/1/read') && (i as RequestInit).method === 'POST')).toBe(true))
  })

  it('locks the options that are required for control reasons and saves the others', async () => {
    const fetchMock = mockFetch(routes({ 'PUT /api/v1/notifications/preferences': () => json(prefs[1]) }))
    renderApp('en', '/notifications')
    const locked = await screen.findByLabelText('Approval assigned to me — Email')
    expect(locked).toBeDisabled()
    const free = screen.getByLabelText('Payment released — Email')
    await waitFor(() => expect(free).toBeEnabled())
    await userEvent.click(free)
    await waitFor(() => expect(sentBody(fetchMock, 'PUT', '/notifications/preferences')).toEqual({ kind: 'PaymentReleased', channel: 'Email', enabled: false }))
  })
})

describe('Notification settings', () => {
  const routes = (extra: Record<string, (init?: RequestInit) => Response> = {}) => ({
    '/api/v1/session': () => json(staffSession),
    ...bell,
    'GET /api/v1/admin/notification-channels/': () => json({ active: null, pending: null }),
    'GET /api/v1/admin/notification-deliveries/summary': () => json({ pending: 1, sent: 5, skipped: 0, deadLetter: 1 }),
    'GET /api/v1/admin/notification-deliveries/dead-letters': () => json([]),
    'GET /api/v1/notification-channel-changes': () => json([]),
    'GET /api/v1/admin/notification-templates': () => json([]),
    ...extra,
  })

  it('asks for the name of a secret, never the secret, and shows delivery counts', async () => {
    mockFetch(routes())
    renderApp('en', '/admin/notifications')
    expect(await screen.findByRole('heading', { name: 'Notification settings', level: 1 })).toBeInTheDocument()
    expect(screen.getByText(/never the secret itself/)).toBeInTheDocument()
    expect(await screen.findByText('Failed permanently')).toBeInTheDocument()
    expect(screen.queryByLabelText(/password/i)).not.toBeInTheDocument()
  })

  it('mirrors in Arabic', async () => {
    mockFetch(routes())
    renderApp('ar', '/admin/notifications')
    expect(await screen.findByRole('heading', { name: 'إعدادات الإشعارات', level: 1 })).toBeInTheDocument()
    expect(document.documentElement.dir).toBe('rtl')
  })
})

describe('Audit evidence', () => {
  const event = { sequence: 1, occurredAt: '2026-10-05T08:00:00Z', action: 'invoice.approved', entityType: 'Invoice', entityId: 'i1', actorType: 'User', actorId: 'u1', summary: 'x' }
  it('verifies an uploaded bundle and reports each check', async () => {
    mockFetch({
      '/api/v1/session': () => json(staffSession),
      ...bell,
      'GET /api/v1/audit/events': () => json({ items: [event], nextAfterSequence: null }),
      'POST /api/v1/audit/evidence/verify': () => json({ isValid: false, eventsChecked: 4, signatureValid: true, filesIntact: false, eventsIntact: true, chainLinked: true, problems: [] }),
    })
    renderApp('en', '/audit')
    expect(await screen.findByRole('link', { name: 'Download evidence bundle' })).toHaveAttribute('href', expect.stringContaining('/api/v1/audit/evidence'))
    await userEvent.upload(screen.getByLabelText('Verify a bundle'), new File(['zip'], 'bundle.zip', { type: 'application/zip' }))
    const result = await screen.findByText('Bundle failed verification')
    const list = within(result.parentElement!)
    expect(list.getByText(/Files unchanged/)).toHaveTextContent('No')
    expect(list.getByText(/Signature valid/)).toHaveTextContent('Yes')
  })
})
