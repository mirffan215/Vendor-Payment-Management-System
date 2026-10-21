import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { json, mockFetch, portalSession, problem, renderApp, sentBody, sentHeader, staffSession } from '../test/helpers'

beforeEach(() => {
  document.documentElement.dir = 'ltr'
})
afterEach(() => vi.unstubAllGlobals())

const entity = { id: 'e1', code: 'KSA', legalName: 'Acme KSA', countryCode: 'SA', baseCurrency: 'SAR', status: 'Active' }
const vendor = { id: 'v1', legalName: 'Al Amal Trading', countryCode: 'SA', status: 'Active', verificationStatus: 'Verified', createdAt: '2026-10-01T08:00:00Z' }

const invoice = (status: string, extra: Record<string, unknown> = {}) => ({
  id: 'i1', legalEntityId: 'e1', vendorId: 'v1', vendorName: 'Al Amal Trading', invoiceNumber: 'INV-100', invoiceDate: '2026-10-01', dueDate: null, currencyCode: 'SAR',
  subtotal: 100, taxTotal: 15, total: 115, poReference: null, supplierTaxInvoiceRef: null, supplierTaxNumber: null, source: 'Email', status, version: 1,
  createdAt: '2026-10-02T08:00:00Z', lines: [{ id: 'l1', lineNo: 1, description: 'Consulting', quantity: 1, unitPrice: 100, lineAmount: 100, taxCode: 'S', taxRate: 0.15, taxAmount: 15 }],
  attachments: [{ id: 'a1', attachmentId: 'f1', role: 'Original', fileName: 'invoice.pdf', scanStatus: 'Clean', sizeBytes: 10 }], ...extra,
})

const exception = (severity: string, rule = 'duplicate.exact') => ({
  id: 'x1', invoiceId: 'i1', invoiceNumber: 'INV-100', legalEntityId: 'e1', ruleCode: rule, severity, status: 'Open', invoiceVersion: 1, createdAt: '2026-10-02T08:00:00Z',
  findings: [{ key: 'duplicate_exact', evidence: { invoiceNumber: 'INV-099', status: 'Verified' } }],
})

describe('invoice list', () => {
  it('lists invoices with translated status and source, and filters by status', async () => {
    const fetchMock = mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/invoices?': () => json([{ id: 'i1', legalEntityId: 'e1', vendorId: 'v1', vendorName: 'Al Amal Trading', invoiceNumber: 'INV-100', invoiceDate: '2026-10-01', currencyCode: 'SAR', total: 115, source: 'Portal', status: 'Exception', createdAt: '2026-10-02T08:00:00Z' }]),
      'GET /api/v1/admin/legal-entities': () => json([entity]),
      'GET /api/v1/vendors': () => json([vendor]),
    })
    renderApp('en', '/invoices')
    const row = (await screen.findByRole('link', { name: 'INV-100' })).closest('tr')!
    expect(within(row).getByText('Needs attention')).toBeInTheDocument()
    expect(within(row).getByText('Vendor portal')).toBeInTheDocument()

    await userEvent.selectOptions(screen.getByLabelText('Status'), 'Verified')
    await userEvent.click(screen.getByRole('button', { name: 'Filter' }))
    await waitFor(() => expect(fetchMock.mock.calls.some(([u]) => String(u).includes('status=Verified'))).toBe(true))
  })

  it('creates a draft invoice and opens it', async () => {
    const fetchMock = mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/invoices?': () => json([]),
      'GET /api/v1/admin/legal-entities': () => json([entity]),
      'GET /api/v1/vendors': () => json([vendor]),
      'POST /api/v1/invoices': () => json(invoice('Draft', { id: 'i9' })),
      'GET /api/v1/invoices/i9/verification': () => json({ invoiceId: 'i9', invoiceStatus: 'Draft', invoiceVersion: 1, ruleSetVersion: 0, results: [], exceptions: [] }),
      'GET /api/v1/invoices/i9/extractions': () => json([]),
      'GET /api/v1/invoices/i9': () => json(invoice('Draft', { id: 'i9' })),
    })
    renderApp('en', '/invoices')
    await userEvent.click(await screen.findByRole('button', { name: 'New invoice' }))
    await userEvent.type(await screen.findByLabelText('Invoice number'), 'INV-555')
    await userEvent.type(screen.getAllByLabelText('Description')[0], 'Consulting')
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/api/v1/invoices')).toMatchObject({ legalEntityId: 'e1', vendorId: 'v1', data: { invoiceNumber: 'INV-555', currencyCode: 'SAR' } }))
    expect(sentHeader(fetchMock, 'POST', '/api/v1/invoices', 'X-XSRF-TOKEN')).toBe('csrf-staff')
    await waitFor(() => expect(window.location.pathname).toBe('/invoices/i9'))
  })
})

describe('invoice detail', () => {
  const routes = (status: string, exceptions: unknown[] = [], extra: Record<string, () => Response> = {}) => ({
    '/api/v1/session': () => json(staffSession),
    'GET /api/v1/invoices/i1/verification': () => json({
      invoiceId: 'i1', invoiceStatus: status, invoiceVersion: 1, ruleSetVersion: 2,
      results: [
        { ruleCode: 'duplicate.exact', outcome: exceptions.length ? 'Failed' : 'Passed', severity: 'Block', findings: exceptions.length ? [{ key: 'duplicate_exact', evidence: { invoiceNumber: 'INV-099', status: 'Verified' } }] : [] },
        { ruleCode: 'po.required', outcome: 'Skipped', severity: 'Flag', findings: [] },
      ],
      exceptions,
    }),
    'GET /api/v1/invoices/i1/extractions': () => json([]),
    'GET /api/v1/invoices/i1': () => json(invoice(status)),
    ...extra,
  })

  it('shows what was checked, and the findings in the user language', async () => {
    mockFetch(routes('Exception', [exception('Block')]))
    renderApp('en', '/invoices/i1')
    expect(await screen.findByText('Same as invoice INV-099 (Verified).')).toBeInTheDocument()
    expect(screen.getByText(/Not run/)).toBeInTheDocument()
    expect(screen.getAllByText('Exact duplicate').length).toBeGreaterThan(0)
  })

  it('shows findings in Arabic and mirrors the page', async () => {
    mockFetch(routes('Exception', [exception('Block')]))
    renderApp('ar', '/invoices/i1')
    expect(await screen.findByText('مطابقة للفاتورة INV-099 (Verified).')).toBeInTheDocument()
    expect(document.documentElement).toHaveAttribute('dir', 'rtl')
  })

  it('accepts an exception only with a reason, and sends it with the antiforgery header', async () => {
    const fetchMock = mockFetch(routes('Exception', [exception('Flag', 'duplicate.same_number')], {
      'POST /api/v1/invoice-exceptions/x1/waive': () => json({}),
    }))
    renderApp('en', '/invoices/i1')
    const button = await screen.findByRole('button', { name: 'Accept with reason' })
    expect(button).toBeDisabled()
    await userEvent.type(screen.getByLabelText('Reason for accepting'), 'Corrected invoice')
    await userEvent.click(button)
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/waive')).toEqual({ reason: 'Corrected invoice' }))
  })

  it('explains in Arabic that a blocking check needs the override permission', async () => {
    mockFetch(routes('Exception', [exception('Block')], { 'POST /api/v1/invoice-exceptions/x1/waive': () => problem(409, 'override_required') }))
    renderApp('ar', '/invoices/i1')
    await userEvent.type(await screen.findByLabelText('سبب القبول'), 'سبب')
    await userEvent.click(screen.getByRole('button', { name: 'قبول مع ذكر السبب' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('قبول فحص مانع يتطلب صلاحية التجاوز.')
  })

  it('offers submit for a draft and edit only while the invoice can be edited', async () => {
    mockFetch(routes('Draft'))
    const view = renderApp('en', '/invoices/i1')
    expect(await screen.findByRole('button', { name: 'Submit' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Edit' })).toBeInTheDocument()
    view.unmount()

    mockFetch(routes('Verified'))
    renderApp('en', '/invoices/i1')
    await screen.findByRole('button', { name: 'Verify again' })
    expect(screen.queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Submit' })).not.toBeInTheDocument()
  })

  it('shows OCR suggestions as suggestions with their confidence, never as values', async () => {
    mockFetch(routes('Draft', [], {
      'GET /api/v1/invoices/i1/extractions': () => json([{ id: 'ex1', engine: 'ocr', status: 'Suggested', result: { engine: 'ocr', fields: { invoiceNumber: { value: 'INV-OCR-9', confidence: 0.62 } }, lines: [] } }]),
    }))
    renderApp('en', '/invoices/i1')
    expect(await screen.findByText(/INV-OCR-9/)).toBeInTheDocument()
    expect(screen.getByText(/62%/)).toBeInTheDocument()
    expect(screen.getByText(/only suggestions/)).toBeInTheDocument()
  })
})

describe('exceptions queue', () => {
  it('lists open exceptions and marks blocking ones', async () => {
    mockFetch({ '/api/v1/session': () => json(staffSession), 'GET /api/v1/invoice-exceptions': () => json([exception('Block')]) })
    renderApp('en', '/exceptions')
    expect(await screen.findByRole('link', { name: 'INV-100' })).toHaveAttribute('href', '/invoices/i1')
    expect(screen.getByText(/Blocking/)).toBeInTheDocument()
    expect(screen.getByText('Same as invoice INV-099 (Verified).')).toBeInTheDocument()
  })
})

describe('verification rules', () => {
  const catalog = [
    { code: 'duplicate.exact', defaultEnabled: true, defaultSeverity: 'Block', canDisable: true, canFlag: true, parameters: [] },
    { code: 'vendor.state', defaultEnabled: true, defaultSeverity: 'Block', canDisable: false, canFlag: false, parameters: [] },
    { code: 'po.match', defaultEnabled: true, defaultSeverity: 'Flag', canDisable: true, canFlag: true, parameters: [{ name: 'priceTolerancePercent', default: 2, min: 0, max: 50 }] },
  ]
  const rules = {
    'duplicate.exact': { enabled: true, severity: 'Block', parameters: {} },
    'vendor.state': { enabled: true, severity: 'Block', parameters: {} },
    'po.match': { enabled: true, severity: 'Flag', parameters: { priceTolerancePercent: 2 } },
  }
  const ruleSet = (extra: Record<string, unknown> = {}) => ({ id: null, legalEntityId: 'e1', versionNo: 0, status: 'Default', config: { rules }, note: '', loosenedRules: [], ...extra })

  it('tells the user when a change loosens a check and needs a second person', async () => {
    const fetchMock = mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/admin/legal-entities/e1/verification-rules': () => json({ active: ruleSet(), pending: null }),
      'GET /api/v1/admin/legal-entities': () => json([entity]),
      'GET /api/v1/verification-rules/catalog': () => json(catalog),
      'GET /api/v1/verification-rule-changes': () => json([]),
      'PUT /api/v1/admin/legal-entities/e1/verification-rules': () => json(ruleSet({ id: 'r1', versionNo: 1, status: 'Pending', loosenedRules: ['duplicate.exact'] })),
    })
    renderApp('en', '/verification')
    const group = (await screen.findByText('Exact duplicate', { selector: 'legend' })).closest('fieldset')!
    // vendor status can never be lowered or switched off
    const vendorGroup = screen.getByText('Vendor status', { selector: 'legend' }).closest('fieldset')!
    expect(within(vendorGroup).getByRole('checkbox')).toBeDisabled()
    expect(within(vendorGroup).getByRole('combobox')).toBeDisabled()

    await userEvent.selectOptions(within(group).getByRole('combobox'), 'Flag')
    await userEvent.type(screen.getByLabelText('Why are you changing the rules?'), 'Resends are common')
    await userEvent.click(screen.getByRole('button', { name: 'Save change' }))

    expect(await screen.findByText(/second person must approve it/)).toBeInTheDocument()
    expect(sentBody(fetchMock, 'PUT', '/verification-rules')).toMatchObject({ note: 'Resends are common', rules: { 'duplicate.exact': { severity: 'Flag' } } })
  })

  it('shows pending loosening changes to an approver with what changes, and approves with the antiforgery header', async () => {
    const loose = { ...rules, 'duplicate.exact': { enabled: true, severity: 'Flag', parameters: {} } }
    const fetchMock = mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/admin/legal-entities/e1/verification-rules': () => problem(403),
      'GET /api/v1/admin/legal-entities': () => json([entity]),
      'GET /api/v1/verification-rules/catalog': () => json(catalog),
      'GET /api/v1/verification-rule-changes': () => json([{
        change: ruleSet({ id: 'r1', versionNo: 1, status: 'Pending', note: 'Resends are common', loosenedRules: ['duplicate.exact'], config: { rules: loose } }), base: ruleSet(),
      }]),
      'POST /api/v1/verification-rule-changes/r1/approve': () => json({}),
    })
    renderApp('en', '/verification')
    expect(await screen.findByText('Resends are common')).toBeInTheDocument()
    expect(screen.getByText(/Blocks → Flag/)).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Approve' }))
    await waitFor(() => expect(sentHeader(fetchMock, 'POST', '/approve', 'X-XSRF-TOKEN')).toBe('csrf-staff'))
  })
})

describe('invoice intake', () => {
  it('assigns an unmatched email to a vendor', async () => {
    const fetchMock = mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/invoice-intake/messages': () => json([{ id: 'm1', legalEntityId: 'e1', fromAddress: 'stranger@nowhere.example', subject: 'Invoice 7', receivedAt: '2026-10-02T08:00:00Z', status: 'Unmatched', attempts: 1, attachmentCount: 2, note: 'Unknown sender' }]),
      'GET /api/v1/vendors': () => json([vendor]),
      'POST /api/v1/invoice-intake/messages/m1/assign': () => json({}),
    })
    renderApp('en', '/intake')
    expect(await screen.findByText('stranger@nowhere.example')).toBeInTheDocument()
    const assign = screen.getByRole('button', { name: 'Assign and create draft' })
    expect(assign).toBeDisabled()
    await userEvent.selectOptions(screen.getByLabelText('Vendor'), 'v1')
    await userEvent.click(assign)
    await waitFor(() => expect(sentBody(fetchMock, 'POST', '/assign')).toEqual({ vendorId: 'v1' }))
  })

  it('imports purchase orders and reports problems per order', async () => {
    const fetchMock = mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/invoice-intake/messages': () => json([]),
      'GET /api/v1/vendors': () => json([]),
      'GET /api/v1/admin/legal-entities': () => json([entity]),
      'GET /api/v1/purchase-orders': () => json([]),
      'POST /api/v1/admin/legal-entities/e1/purchase-orders/import': () => json({ created: 1, updated: 0, unchanged: 0, errors: [{ poNumber: 'PO-9', code: 'po_older_version', message: 'x' }] }),
    })
    renderApp('en', '/intake')
    await userEvent.click(await screen.findByRole('tab', { name: 'Purchase orders' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Import' }))
    expect(await screen.findByText('1 created, 0 updated, 0 unchanged.')).toBeInTheDocument()
    expect(screen.getByText(/A newer version of this purchase order is already stored/)).toBeInTheDocument()
    expect(Array.isArray((sentBody(fetchMock, 'POST', '/import') as { purchaseOrders: unknown[] }).purchaseOrders)).toBe(true)
  })

  it('rejects malformed JSON before calling the server', async () => {
    const fetchMock = mockFetch({
      '/api/v1/session': () => json(staffSession),
      'GET /api/v1/invoice-intake/messages': () => json([]),
      'GET /api/v1/vendors': () => json([]),
      'GET /api/v1/admin/legal-entities': () => json([entity]),
      'GET /api/v1/purchase-orders': () => json([]),
    })
    renderApp('en', '/intake')
    await userEvent.click(await screen.findByRole('tab', { name: 'Purchase orders' }))
    const box = await screen.findByLabelText('Purchase orders (JSON)')
    await userEvent.clear(box)
    await userEvent.type(box, 'not json')
    await userEvent.click(screen.getByRole('button', { name: 'Import' }))
    expect(await screen.findByText('This is not valid JSON.')).toBeInTheDocument()
    expect(fetchMock.mock.calls.some(([u]) => String(u).includes('/import'))).toBe(false)
  })
})

describe('vendor portal invoices', () => {
  it('shows the simplified status and submits an invoice as multipart', async () => {
    const fetchMock = mockFetch({
      '/api/v1/portal/session': () => json(portalSession),
      'GET /api/v1/portal/profile': () => json({ id: 'v1', legalName: 'Al Amal Trading', countryCode: 'SA', status: 'Active', verificationStatus: 'Verified' }),
      'GET /api/v1/portal/entities': () => json([{ legalEntityId: 'e1', legalEntityName: 'Acme KSA', defaultCurrency: 'SAR', status: 'Active' }]),
      'GET /api/v1/portal/invoices': () => json([{ id: 'i1', invoiceNumber: 'P-1', invoiceDate: '2026-10-01', currencyCode: 'SAR', total: 115, status: 'NeedsAttention', entityName: 'Acme KSA', legalEntityId: 'e1', createdAt: '2026-10-02T08:00:00Z' }]),
      'POST /api/v1/portal/invoices': () => json({}),
    })
    renderApp('en', '/portal')
    await userEvent.click(await screen.findByRole('tab', { name: 'Invoices' }))
    expect(await screen.findByText('Needs attention: the customer will contact you')).toBeInTheDocument()

    await userEvent.type(screen.getByLabelText('Invoice number'), 'P-2')
    await userEvent.type(screen.getByLabelText('Subtotal'), '100')
    await userEvent.type(screen.getByLabelText('Tax total'), '15')
    await userEvent.type(screen.getByLabelText('Total'), '115')
    await userEvent.upload(screen.getByLabelText('File'), new File(['%PDF-1.7'], 'inv.pdf', { type: 'application/pdf' }))
    await userEvent.click(screen.getByRole('button', { name: 'Send invoice' }))

    await waitFor(() => {
      const call = fetchMock.mock.calls.find(([u, i]) => String(u) === '/api/v1/portal/invoices' && (i as RequestInit).method === 'POST')!
      const body = (call[1] as RequestInit).body as FormData
      expect(body.get('invoiceNumber')).toBe('P-2')
      expect(body.get('legalEntityId')).toBe('e1')
      expect(body.get('total')).toBe('115')
      expect(body.get('file')).toBeInstanceOf(File)
    })
  })
})
