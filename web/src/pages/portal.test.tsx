import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { json, mockFetch, portalSession, problem, renderApp, sentBody, sentHeader } from '../test/helpers'

beforeEach(() => {
  document.documentElement.dir = 'ltr'
})
afterEach(() => vi.unstubAllGlobals())

const invite = { email: 'ap@amal.example', displayName: 'AP Clerk', vendorName: 'Al Amal Trading', purpose: 'Invite', needsAuthenticator: true }

describe('portal invitation', () => {
  it('walks through password, authenticator set-up and recovery codes without touching the staff API', async () => {
    const fetchMock = mockFetch({
      '/api/v1/portal/session': () => problem(401),
      'GET /portal/auth/invite?': () => json(invite),
      'POST /portal/auth/invite/accept': () => json({ completed: false, enrollment: { secret: 'JBSWY3DPEHPK3PXP', provisioningUri: 'otpauth://totp/VPMS:ap?secret=JBSWY3DPEHPK3PXP&issuer=VPMS' } }),
      'POST /portal/auth/invite/enroll': () => json({ recoveryCodes: ['AAAA-1111', 'BBBB-2222'] }),
    })
    renderApp('en', '/portal/accept?tenant=acme&token=tok')

    expect(await screen.findByRole('heading', { name: 'Welcome to the Al Amal Trading portal' })).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('New password'), 'purple elephants dance quietly')
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }))

    expect(await screen.findByText('JBSWY3DPEHPK3PXP')).toBeInTheDocument()
    expect(screen.getByRole('img', { name: 'QR code for your authenticator app' }).querySelector('svg')).not.toBeNull()
    await userEvent.type(screen.getByLabelText('6-digit code'), '123456')
    await userEvent.click(screen.getByRole('button', { name: 'Verify' }))

    expect(await screen.findByText('AAAA-1111')).toBeInTheDocument()
    expect(sentBody(fetchMock, 'POST', '/portal/auth/invite/accept')).toEqual({ tenant: 'acme', token: 'tok', password: 'purple elephants dance quietly' })
    expect(fetchMock.mock.calls.some(([u]) => String(u).startsWith('/api/v1/session'))).toBe(false)
  })

  it('says nothing about why a link failed', async () => {
    mockFetch({ '/api/v1/portal/session': () => problem(401), 'GET /portal/auth/invite?': () => problem(404, 'not_found') })
    renderApp('ar', '/portal/accept?tenant=acme&token=bad')
    expect(await screen.findByRole('alert')).toHaveTextContent('هذا الرابط غير صالح أو منتهي الصلاحية')
    expect(document.documentElement).toHaveAttribute('dir', 'rtl')
  })
})

describe('portal sign-in', () => {
  it('asks for the authenticator code after the password and shows one message for every failure', async () => {
    let mfaAttempts = 0
    mockFetch({
      '/api/v1/portal/session': () => problem(401),
      'POST /portal/auth/login': () => json({ challenge: 'chal' }),
      'POST /portal/auth/mfa': () => (mfaAttempts++ === 0 ? problem(401, 'invalid_credentials') : new Response(null, { status: 204 })),
    })
    renderApp('en', '/portal/login')
    await userEvent.type(await screen.findByLabelText('Organisation identifier'), 'acme')
    await userEvent.type(screen.getByLabelText('Email'), 'ap@amal.example')
    await userEvent.type(screen.getByLabelText('Password'), 'purple elephants dance quietly')
    await userEvent.click(screen.getByRole('button', { name: 'Continue' }))

    await userEvent.type(await screen.findByLabelText('6-digit code'), '000000')
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Sign-in failed. Check your details and try again.')
  })

  it('unauthenticated visitors to the portal are sent to portal sign-in, not the staff one', async () => {
    mockFetch({ '/api/v1/portal/session': () => problem(401) })
    renderApp('en', '/portal')
    expect(await screen.findByRole('heading', { name: 'Vendor sign-in' })).toBeInTheDocument()
    expect(window.location.pathname).toBe('/portal/login')
  })
})

describe('portal home', () => {
  const routes = (extra: Record<string, () => Response> = {}) => ({
    '/api/v1/portal/session': () => json(portalSession),
    'GET /api/v1/portal/profile': () => json({ id: 'v1', legalName: 'Al Amal Trading', countryCode: 'SA', status: 'Active', verificationStatus: 'Verified', contactEmail: 'old@amal.example' }),
    'GET /api/v1/portal/entities': () => json([{ legalEntityId: 'e1', legalEntityName: 'Acme KSA', defaultCurrency: 'SAR', status: 'Active' }]),
    ...extra,
  })

  it('shows the profile with the legal name locked and saves the editable fields with the antiforgery header', async () => {
    const fetchMock = mockFetch(routes({ 'PUT /api/v1/portal/profile': () => json({ id: 'v1', legalName: 'Al Amal Trading', countryCode: 'SA', status: 'Active', verificationStatus: 'Verified' }) }))
    renderApp('en', '/portal')
    expect(await screen.findByText('Welcome, AP Clerk — Al Amal Trading')).toBeInTheDocument()
    expect(await screen.findByText(/Acme KSA/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Legal name')).not.toBeInTheDocument() // not editable by the vendor

    await userEvent.type(screen.getByLabelText('Trade name'), 'Amal Co')
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))
    await screen.findByText('Saved.')
    expect(sentBody(fetchMock, 'PUT', '/api/v1/portal/profile')).toMatchObject({ tradeName: 'Amal Co', contactEmail: 'old@amal.example' })
    expect(sentHeader(fetchMock, 'PUT', '/api/v1/portal/profile', 'X-XSRF-TOKEN')).toBe('csrf-portal')
  })

  it('requests a bank change, never sees reviewer details, and gets a generic message for a refused account', async () => {
    const fetchMock = mockFetch(routes({
      'GET /api/v1/portal/bank-details': () => json([{ id: 'b1', bankName: 'National Bank', accountName: 'Al Amal', currencyCode: 'SAR', maskedAccount: '••••0001', status: 'Verified' }]),
      'GET /api/v1/portal/bank-detail-changes': () => json([]),
      'POST /api/v1/portal/bank-detail-changes': () => problem(409, 'bank_details_not_accepted'),
    }))
    renderApp('en', '/portal')
    await userEvent.click(await screen.findByRole('tab', { name: 'Bank details' }))
    expect(await screen.findByText(/••••0001/)).toBeInTheDocument()

    await userEvent.type(screen.getByLabelText('Account holder'), 'Al Amal')
    await userEvent.type(screen.getByLabelText('Bank'), 'New Bank')
    await userEvent.type(screen.getByLabelText('IBAN'), 'SA0380000000608010167519')
    await userEvent.type(screen.getByLabelText('Reason for the change'), 'Moved banks')
    await userEvent.click(screen.getByRole('button', { name: 'Send request' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('These bank details cannot be accepted online. Please contact the customer.')
    expect(sentBody(fetchMock, 'POST', '/bank-detail-changes')).toMatchObject({ iban: 'SA0380000000608010167519', currencyCode: 'SAR' })
    expect(sentBody(fetchMock, 'POST', '/bank-detail-changes')).not.toHaveProperty('confirmSharedAccount')
  })

  it('replies to a request from the customer', async () => {
    let replied = false
    const fetchMock = mockFetch(routes({
      'GET /api/v1/portal/requests': () => json([{ id: 'r1', subject: 'Updated tax certificate', status: replied ? 'Responded' : 'Open', createdAt: '2026-10-01T08:00:00Z',
        messages: [{ id: 'm1', direction: 'ToVendor', body: 'Please upload it.', createdAt: '2026-10-01T08:00:00Z' }] }]),
      'POST /api/v1/portal/requests/r1/reply': () => {
        replied = true
        return json({})
      },
    }))
    renderApp('en', '/portal')
    await userEvent.click(await screen.findByRole('tab', { name: 'Messages' }))
    expect(await screen.findByText('Updated tax certificate')).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Reply'), 'Done, thanks')
    await userEvent.click(screen.getByRole('button', { name: 'Send reply' }))
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/reply')).toEqual({ body: 'Done, thanks' }))
  })

  it('uploads a document as multipart and offers download only for clean files', async () => {
    const fetchMock = mockFetch(routes({
      'GET /api/v1/portal/documents': () => json([
        { id: 'd1', type: 'TaxCertificate', fileName: 'cert.pdf', scanStatus: 'Clean', status: 'Uploaded', createdAt: '2026-10-01T08:00:00Z' },
        { id: 'd2', type: 'Other', fileName: 'new.pdf', scanStatus: 'Pending', status: 'Uploaded', createdAt: '2026-10-02T08:00:00Z' },
      ]),
      'POST /api/v1/portal/documents': () => json({}),
    }))
    renderApp('en', '/portal')
    await userEvent.click(await screen.findByRole('tab', { name: 'Documents' }))
    expect(await screen.findAllByRole('link', { name: 'Download' })).toHaveLength(1)

    await userEvent.upload(screen.getByLabelText('File'), new File(['%PDF-1.7'], 'a.pdf', { type: 'application/pdf' }))
    await userEvent.click(screen.getByRole('button', { name: 'Upload' }))
    await waitFor(() => {
      const call = fetchMock.mock.calls.find(([u, i]) => String(u) === '/api/v1/portal/documents' && (i as RequestInit).method === 'POST')!
      expect((call[1] as RequestInit).body).toBeInstanceOf(FormData)
    })
  })
})
