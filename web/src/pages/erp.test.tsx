import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { json, mockFetch, problem, renderApp, sentBody, staffSession } from '../test/helpers'

beforeEach(() => {
  document.documentElement.dir = 'ltr'
})
afterEach(() => vi.unstubAllGlobals())

const entity = { id: 'e1', code: 'KSA', legalName: 'Acme KSA', countryCode: 'SA', baseCurrency: 'SAR', status: 'Active' }

const connection = (status = 'Active', extra: Record<string, unknown> = {}) => ({
  id: 'c1', kind: 'Connection', legalEntityId: null, versionNo: 1, status, mapping: null, note: 'Initial', createdBy: 'u9', createdAt: '2026-10-01T08:00:00Z',
  decidedBy: null, decidedAt: null, decisionComment: null,
  connection: { provider: 'D365Finance', baseUrl: 'https://contoso.operations.dynamics.com', authorityTenantId: 'tenant-1', clientId: 'client-1', secretRef: 'erp-secret', entitySets: {}, pageSize: 200, timeoutSeconds: 60 },
  ...extra,
})

const mapping = (status = 'Active') => ({
  id: 'm1', kind: 'Mapping', legalEntityId: 'e1', versionNo: 1, status, connection: null, note: 'Initial', createdBy: 'u9', createdAt: '2026-10-01T08:00:00Z', decidedBy: null, decidedAt: null, decisionComment: null,
  mapping: { company: 'USMF', journalName: 'VENDINV', postingProfile: null, paymentTermsMap: { N30: 'Net30' }, taxCodeMap: {}, dimensions: [{ vpmsDimension: 'CostCenter', erpDimension: 'CostCentre', defaultValue: null, valueMap: { '100': 'CC-100' } }] },
})

describe('ERP connection and mapping', () => {
  const routes = (extra: Record<string, (init?: RequestInit) => Response> = {}) => ({
    '/api/v1/session': () => json(staffSession),
    'GET /api/v1/admin/legal-entities/e1/erp-mapping': () => json({ active: mapping(), pending: null }),
    'GET /api/v1/admin/legal-entities': () => json([entity]),
    'GET /api/v1/admin/erp/connection': () => json({ active: connection(), pending: null }),
    'GET /api/v1/erp-changes': () => json([]),
    ...extra,
  })

  it('shows the connection in force and never asks for the secret itself', async () => {
    mockFetch(routes())
    renderApp('en', '/erp')
    expect(await screen.findByText('In force: version 1')).toBeInTheDocument()
    expect(screen.getByLabelText('Name of the secret in the vault')).toHaveValue('erp-secret')
    expect(screen.getByText(/Enter the name of the secret, never the secret itself/)).toBeInTheDocument()
    expect(screen.queryByLabelText(/password|client secret/i)).not.toBeInTheDocument()
  })

  it('proposes a connection change only with a reason and sends just the entity names that were filled in', async () => {
    const fetchMock = mockFetch(routes({ 'PUT /api/v1/admin/erp/connection': () => json(connection('Pending', { id: 'c2', versionNo: 2 })) }))
    renderApp('en', '/erp')
    const [propose] = await screen.findAllByRole('button', { name: 'Propose change' })
    expect(propose).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Vendors'), 'MyVendors')
    await userEvent.type(screen.getAllByLabelText('Why is this changing?')[0], 'Use our custom vendor entity')
    await userEvent.click(propose)
    await waitFor(() => expect(sentBody(fetchMock, 'PUT', '/api/v1/admin/erp/connection')).toMatchObject({
      note: 'Use our custom vendor entity', config: { provider: 'D365Finance', secretRef: 'erp-secret', entitySets: { vendors: 'MyVendors' } },
    }))
  })

  it('blocks a second proposal while one is waiting and says it is waiting', async () => {
    mockFetch(routes({ 'GET /api/v1/admin/erp/connection': () => json({ active: connection(), pending: connection('Pending', { id: 'c2', versionNo: 2 }) }) }))
    renderApp('en', '/erp')
    expect(await screen.findByText('Waiting for approval: version 2')).toBeInTheDocument()
    expect(screen.getAllByRole('button', { name: 'Propose change' })[0]).toBeDisabled()
  })

  it('turns the mapping text boxes into maps and dimensions', async () => {
    const fetchMock = mockFetch(routes({ 'PUT /api/v1/admin/legal-entities/e1/erp-mapping': () => json(mapping('Pending')) }))
    renderApp('en', '/erp')
    expect(await screen.findByDisplayValue('USMF')).toBeInTheDocument()
    expect(screen.getByDisplayValue('N30=Net30')).toBeInTheDocument()
    await userEvent.type(screen.getByLabelText('Tax codes, one per line as VPMS=ERP'), 'S=VAT15')
    await userEvent.type(screen.getAllByLabelText('Why is this changing?')[1], 'Add the VAT code')
    await userEvent.click(screen.getAllByRole('button', { name: 'Propose change' })[1])
    await waitFor(() => expect(sentBody(fetchMock, 'PUT', '/erp-mapping')).toMatchObject({
      note: 'Add the VAT code',
      config: { company: 'USMF', journalName: 'VENDINV', postingProfile: null, paymentTermsMap: { N30: 'Net30' }, taxCodeMap: { S: 'VAT15' }, dimensions: [{ vpmsDimension: 'CostCenter', erpDimension: 'CostCentre', valueMap: { '100': 'CC-100' } }] },
    }))
  })

  it('shows a refused proposal with its translated reason', async () => {
    mockFetch(routes({ 'PUT /api/v1/admin/erp/connection': () => problem(400, 'secret_ref_invalid') }))
    renderApp('en', '/erp')
    await userEvent.type(await screen.findByLabelText('Why is this changing?', { selector: 'input' }), 'x')
    await userEvent.click(screen.getAllByRole('button', { name: 'Propose change' })[0])
    expect(await screen.findByText('Give the name of the secret in the vault, not the secret itself.')).toBeInTheDocument()
  })

  it('tests the connection and reports success or the reason for failure', async () => {
    let ok = true
    mockFetch(routes({ 'POST /api/v1/admin/legal-entities/e1/erp/test': () => json(ok ? { ok: true, errorCode: null, message: null } : { ok: false, errorCode: 'secret_unavailable', message: null }) }))
    renderApp('en', '/erp')
    await userEvent.click(await screen.findByRole('button', { name: 'Test connection' }))
    expect(await screen.findByText('The ERP answered.')).toBeInTheDocument()
    ok = false
    await userEvent.click(screen.getByRole('button', { name: 'Test connection' }))
    expect(await screen.findByText('The test failed: The secret could not be read from the vault.')).toBeInTheDocument()
  })

  it('shows what a change alters and lets a second person approve or reject it', async () => {
    const pending = connection('Pending', { id: 'c2', versionNo: 2, note: 'Rotate the secret', connection: { ...connection().connection, secretRef: 'erp-secret-2' } })
    const fetchMock = mockFetch(routes({
      'GET /api/v1/erp-changes': () => json([{ change: pending, base: connection() }]),
      'POST /api/v1/erp-changes/c2/approve': () => json(connection('Active', { id: 'c2', versionNo: 2 })),
      'POST /api/v1/erp-changes/c2/reject': () => json(connection('Rejected', { id: 'c2', versionNo: 2 })),
    }))
    renderApp('en', '/erp')
    const card = (await screen.findByText('Rotate the secret')).closest('li')!
    const table = within(card).getByRole('table')
    expect(within(table).getByText('secretRef')).toBeInTheDocument()
    expect(within(table).getByText('"erp-secret-2"')).toBeInTheDocument()
    expect(within(table).queryByText('baseUrl')).not.toBeInTheDocument()

    const reject = within(card).getByRole('button', { name: 'Reject' })
    expect(reject).toBeDisabled()
    await userEvent.type(within(card).getByLabelText('Comment'), 'Wrong vault')
    await userEvent.click(reject)
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/erp-changes/c2/reject')).toEqual({ comment: 'Wrong vault' }))
  })
})

describe('sync monitor', () => {
  const record = (extra: Record<string, unknown> = {}) => ({
    id: 's1', legalEntityId: 'e1', type: 'PostInvoice', objectId: 'i1', objectVersion: '1', status: 'DeadLetter', attempts: 1, nextAttemptAt: '2026-10-02T08:00:00Z',
    lastErrorCode: 'tax_code_unmapped', lastError: 'Tax code Z on line 1 has no ERP mapping.', erpReference: null, createdAt: '2026-10-02T07:00:00Z', completedAt: null, handledBy: null, handlingNote: null, ...extra,
  })

  const routes = (extra: Record<string, (init?: RequestInit) => Response> = {}) => ({
    '/api/v1/session': () => json(staffSession),
    'GET /api/v1/admin/legal-entities': () => json([entity]),
    'GET /api/v1/erp/sync/summary': () => json({ pending: 2, inFlight: 0, succeeded: 40, deadLetter: 1, resolved: 3 }),
    'GET /api/v1/erp/sync/s1': () => json({ record: record(), attempts: [{ attemptNo: 1, at: '2026-10-02T07:05:00Z', outcome: 'Rejected', code: 'tax_code_unmapped', message: 'Tax code Z' }] }),
    'GET /api/v1/erp/sync': () => json([record()]),
    ...extra,
  })

  it('shows the counts, lists what needs attention first and explains the cause', async () => {
    const fetchMock = mockFetch(routes())
    renderApp('en', '/erp/sync')
    expect(await screen.findByText('A tax code has no ERP mapping.')).toBeInTheDocument()
    expect(screen.getByText('Need attention').querySelector('strong')).toHaveTextContent('1')
    expect(fetchMock.mock.calls.some(([u]) => String(u).includes('/api/v1/erp/sync?status=DeadLetter'))).toBe(true)
  })

  it('retries a dead-lettered message only with a reason', async () => {
    const fetchMock = mockFetch(routes({ 'POST /api/v1/erp/sync/s1/retry': () => json(record({ status: 'Pending' })) }))
    renderApp('en', '/erp/sync')
    await userEvent.click(await screen.findByRole('button', { name: 'Details' }))
    const retry = await screen.findByRole('button', { name: 'Send again' })
    expect(retry).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Reason'), 'Mapped the tax code')
    await userEvent.click(retry)
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/erp/sync/s1/retry')).toEqual({ reason: 'Mapped the tax code' }))
  })

  it('marks a message as handled by hand with the ERP reference', async () => {
    const fetchMock = mockFetch(routes({ 'POST /api/v1/erp/sync/s1/resolve': () => json(record({ status: 'Resolved' })) }))
    renderApp('en', '/erp/sync')
    await userEvent.click(await screen.findByRole('button', { name: 'Details' }))
    await userEvent.type(await screen.findByLabelText('Reason'), 'Posted by hand')
    await userEvent.type(screen.getByLabelText('ERP reference (optional)'), 'JB-77')
    await userEvent.click(screen.getByRole('button', { name: 'Mark as handled' }))
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/erp/sync/s1/resolve')).toEqual({ reason: 'Posted by hand', erpReference: 'JB-77' }))
  })
})

describe('reconciliation', () => {
  const item = (extra: Record<string, unknown> = {}) => ({
    id: 'x1', legalEntityId: 'e1', type: 'AmountMismatch', invoiceId: 'i1', invoiceNumber: 'INV-100', erpReference: 'JB-1', vpmsValue: '115.00 SAR', erpValue: '999.00 SAR', status: 'Open',
    firstSeenAt: '2026-10-02T08:00:00Z', lastSeenAt: '2026-10-03T08:00:00Z', proposedBy: null, proposedReason: null, decidedBy: null, decidedAt: null, decisionReason: null, ...extra,
  })

  const routes = (extra: Record<string, (init?: RequestInit) => Response> = {}) => ({
    '/api/v1/session': () => json(staffSession),
    'GET /api/v1/admin/legal-entities': () => json([entity]),
    'GET /api/v1/erp/reconciliation/summary': () => json({ open: 1, pendingAcceptance: 0, accepted: 0, resolved: 0, openByType: { AmountMismatch: 1 } }),
    'GET /api/v1/erp/reconciliation/items': () => json([item()]),
    ...extra,
  })

  it('runs the comparison and reports how many differences are open', async () => {
    const fetchMock = mockFetch(routes({
      'POST /api/v1/erp/reconciliation/run': () => json({ id: 'r1', legalEntityId: 'e1', startedAt: '2026-10-03T08:00:00Z', completedAt: '2026-10-03T08:00:05Z', status: 'Completed', errorCode: null, vpmsInvoices: 5, erpDocuments: 6, newItems: 1, openItems: 1, clearedItems: 0 }),
    }))
    renderApp('en', '/erp/reconciliation')
    await userEvent.click(await screen.findByRole('button', { name: 'Run reconciliation' }))
    expect(await screen.findByText('Compared 5 VPMS invoices with 6 ERP documents: 1 open differences.')).toBeInTheDocument()
    expect(sentBody(fetchMock, 'POST', '/erp/reconciliation/run')).toEqual({ legalEntityId: 'e1' })
  })

  it('says why a run failed', async () => {
    mockFetch(routes({
      'POST /api/v1/erp/reconciliation/run': () => json({ id: 'r1', legalEntityId: 'e1', startedAt: '2026-10-03T08:00:00Z', completedAt: null, status: 'Failed', errorCode: 'connection_missing', vpmsInvoices: 0, erpDocuments: 0, newItems: 0, openItems: 0, clearedItems: 0 }),
    }))
    renderApp('en', '/erp/reconciliation')
    await userEvent.click(await screen.findByRole('button', { name: 'Run reconciliation' }))
    expect(await screen.findByText('The run failed: No approved connection.')).toBeInTheDocument()
  })

  it('lists each difference with both values and offers a CSV download', async () => {
    mockFetch(routes())
    renderApp('en', '/erp/reconciliation')
    const card = (await screen.findByText('Different amount', { selector: 'strong' })).closest('li')!
    expect(within(card).getByText('999.00 SAR')).toBeInTheDocument()
    expect(within(card).getByText('115.00 SAR')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Download CSV' })).toHaveAttribute('href', '/api/v1/erp/reconciliation/export?status=Open')
  })

  it('needs a reason to resolve or to propose accepting a difference', async () => {
    const fetchMock = mockFetch(routes({
      'POST /api/v1/erp/reconciliation/items/x1/resolve': () => json(item({ status: 'Resolved' })),
      'POST /api/v1/erp/reconciliation/items/x1/propose-acceptance': () => json(item({ status: 'PendingAcceptance' })),
    }))
    renderApp('en', '/erp/reconciliation')
    const resolve = await screen.findByRole('button', { name: 'Mark as resolved' })
    const propose = screen.getByRole('button', { name: 'Propose to accept' })
    expect(resolve).toBeDisabled()
    expect(propose).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Reason'), 'Agreed with the supplier')
    await userEvent.click(propose)
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/propose-acceptance')).toEqual({ reason: 'Agreed with the supplier' }))
  })

  it('lets a second person confirm or reject a proposed acceptance', async () => {
    const fetchMock = mockFetch(routes({
      'GET /api/v1/erp/reconciliation/items': () => json([item({ status: 'PendingAcceptance', proposedBy: 'u9', proposedReason: 'Rounding' })]),
      'POST /api/v1/erp/reconciliation/items/x1/approve-acceptance': () => json(item({ status: 'Accepted' })),
    }))
    renderApp('en', '/erp/reconciliation')
    expect(await screen.findByText('Proposed to accept: Rounding')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Mark as resolved' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Confirm acceptance' }))
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/approve-acceptance')).toEqual({ comment: '' }))
  })

  it('shows a refused decision with its translated reason', async () => {
    mockFetch(routes({ 'POST /api/v1/erp/reconciliation/items/x1/resolve': () => problem(409, 'invalid_transition') }))
    renderApp('en', '/erp/reconciliation')
    await userEvent.type(await screen.findByLabelText('Reason'), 'done')
    await userEvent.click(screen.getByRole('button', { name: 'Mark as resolved' }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
  })

  it('renders in Arabic, mirrored', async () => {
    mockFetch(routes())
    renderApp('ar', '/erp/reconciliation')
    expect(await screen.findByRole('heading', { name: 'المطابقة', level: 1 })).toBeInTheDocument()
    expect(document.documentElement.dir).toBe('rtl')
    expect(await screen.findByText('مبلغ مختلف', { selector: 'strong' })).toBeInTheDocument()
  })
})
