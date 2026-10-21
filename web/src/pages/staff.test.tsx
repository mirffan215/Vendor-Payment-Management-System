import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { json, mockFetch, problem, renderApp, sentBody, sentHeader, staffSession } from '../test/helpers'

beforeEach(() => {
  document.documentElement.dir = 'ltr'
})
afterEach(() => vi.unstubAllGlobals())

const change = {
  id: 'c1', vendorId: 'v1', vendorName: 'Al Amal Trading', reason: 'New account', source: 'VendorPortal', madeAt: '2026-10-01T08:00:00Z',
  expiresAt: '2026-10-15T08:00:00Z', status: 'Pending', requiresCallback: true,
  proposed: { id: 'b2', accountName: 'Al Amal', bankName: 'National Bank', countryCode: 'SA', currencyCode: 'SAR', maskedAccount: '••••9123', status: 'Pending', createdAt: '2026-10-01T08:00:00Z' },
  current: { id: 'b1', accountName: 'Al Amal', bankName: 'Old Bank', countryCode: 'SA', currencyCode: 'SAR', maskedAccount: '••••0001', status: 'Verified', createdAt: '2026-01-01T08:00:00Z' },
}

describe('bank-detail approval queue', () => {
  it('shows masked values, requires a callback reference note, and sends the approval with the antiforgery header', async () => {
    let decided = false
    const fetchMock = mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/bank-detail-changes': () => json(decided ? [] : [change]),
      'POST /api/v1/bank-detail-changes/c1/approve': () => {
        decided = true
        return json({ ...change, status: 'Approved' })
      },
    })
    renderApp('en', '/bank-changes')

    expect(await screen.findByText(/Call the vendor on a number already on file/)).toBeInTheDocument()
    expect(screen.getByText('••••9123')).toBeInTheDocument()
    expect(screen.getByText('Requested by the vendor')).toBeInTheDocument()

    await userEvent.type(screen.getByLabelText('Callback reference'), 'CB-77')
    await userEvent.click(screen.getByRole('button', { name: 'Approve' }))

    await screen.findByText('Nothing is waiting for approval.')
    expect(sentBody(fetchMock, 'POST', '/approve')).toEqual({ callbackVerificationRef: 'CB-77', comment: null })
    expect(sentHeader(fetchMock, 'POST', '/approve', 'X-XSRF-TOKEN')).toBe('csrf-staff')
  })

  it('explains in Arabic that the same person cannot approve, using the stable error code', async () => {
    mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/bank-detail-changes': () => json([change]),
      'POST /api/v1/bank-detail-changes/c1/approve': () => problem(403, 'sod_violation'),
    })
    renderApp('ar', '/bank-changes')
    await userEvent.click(await screen.findByRole('button', { name: 'اعتماد' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('لا يمكنك تنفيذ هذا لأنك شاركت في العنصر نفسه')
  })

  it('cannot reject without a comment', async () => {
    mockFetch({ '/api/v1/session': () => json(staffSession), 'GET /api/v1/bank-detail-changes': () => json([change]) })
    renderApp('en', '/bank-changes')
    expect(await screen.findByRole('button', { name: 'Reject' })).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Comment'), 'Does not match')
    expect(screen.getByRole('button', { name: 'Reject' })).toBeEnabled()
  })
})

describe('vendors', () => {
  const vendor = (status: string) => ({
    vendor: { id: 'v1', legalName: 'Al Amal Trading', countryCode: 'SA', taxId: '300012345600003', status, verificationStatus: 'Pending', createdAt: '2026-10-01T08:00:00Z' },
    documents: [{ id: 'd1', type: 'TaxCertificate', fileName: 'cert.pdf', scanStatus: 'Clean', status: 'Uploaded', createdAt: '2026-10-01T08:00:00Z' }],
    links: [],
  })

  it('lists vendors and creates one', async () => {
    const fetchMock = mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/vendors': () => json([{ id: 'v1', legalName: 'Al Amal Trading', countryCode: 'SA', status: 'Active', verificationStatus: 'Verified', createdAt: '2026-10-01T08:00:00Z' }]),
      'POST /api/v1/vendors': () => json({ id: 'v2' }),
    })
    renderApp('en', '/vendors')
    expect(await screen.findByRole('link', { name: 'Al Amal Trading' })).toHaveAttribute('href', '/vendors/v1')

    await userEvent.type(screen.getByLabelText('Legal name'), 'New Supplier')
    await userEvent.click(screen.getByRole('button', { name: 'Create vendor' }))
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/api/v1/vendors')).toMatchObject({ legalName: 'New Supplier', countryCode: 'SA' }))
  })

  it('offers only the actions that fit the status, and rejecting needs a reason', async () => {
    const fetchMock = mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/vendors/v1/bank-details': () => json([]),
      'GET /api/v1/vendors/v1/requests': () => json([]),
      'GET /api/v1/vendors/v1': () => json(vendor('UnderReview')),
      'POST /api/v1/vendors/v1/reject': () => json({}),
    })
    renderApp('en', '/vendors/v1')
    expect(await screen.findByRole('heading', { name: 'Al Amal Trading' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Approve vendor' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Submit for review' })).not.toBeInTheDocument()

    const reject = screen.getAllByRole('button', { name: 'Reject' })[0]
    expect(reject).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Reason'), 'Tax number mismatch')
    expect(reject).toBeEnabled()
    await userEvent.click(reject)
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/reject')).toEqual({ reason: 'Tax number mismatch' }))
  })

  it('shows a segregation-of-duties refusal for approving your own vendor', async () => {
    mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/vendors/v1/bank-details': () => json([]),
      'GET /api/v1/vendors/v1/requests': () => json([]),
      'GET /api/v1/vendors/v1': () => json(vendor('UnderReview')),
      'POST /api/v1/vendors/v1/approve': () => problem(403, 'sod_violation'),
    })
    renderApp('en', '/vendors/v1')
    await userEvent.click(await screen.findByRole('button', { name: 'Approve vendor' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('You cannot do this because you took part in the same item')
  })
})

describe('administration', () => {
  it('switches between tabs and toggles a user', async () => {
    const fetchMock = mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/admin/legal-entities': () => json([{ id: 'e1', code: 'KSA', legalName: 'Acme KSA', countryCode: 'SA', baseCurrency: 'SAR', status: 'Active' }]),
      'GET /api/v1/admin/users': () => json([{ id: 'u2', email: 'bob@acme.com', displayName: 'Bob', status: 'Active', roles: ['Vendor Manager'] }]),
      'GET /api/v1/admin/roles': () => json([{ id: 'r1', name: 'Vendor Manager', isSystem: true, permissions: ['Vendor.Edit'], assignmentCount: 1 }]),
      'POST /api/v1/admin/users/u2/disable': () => json({}),
    })
    renderApp('en', '/admin')
    expect(await screen.findByText('Acme KSA')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('tab', { name: 'Users' }))
    const row = (await screen.findByText('bob@acme.com')).closest('tr')!
    await userEvent.click(within(row).getByRole('button', { name: 'Disable' }))
    await waitFor(() => expect(sentHeader(fetchMock, 'POST', '/disable', 'X-XSRF-TOKEN')).toBe('csrf-staff'))

    await userEvent.click(screen.getByRole('tab', { name: 'Roles' }))
    expect(await screen.findByText('Vendor.Edit')).toBeInTheDocument()
  })
})
