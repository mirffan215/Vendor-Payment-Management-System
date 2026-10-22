import { expect, test, type Page } from '@playwright/test'

const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })

async function staff(page: Page) {
  await page.route('**/api/v1/session', (r) => r.fulfill(json({ userId: 'u', tenantId: 't', email: 'a@acme.com', displayName: 'Alice', locale: 'en', csrfToken: 'x' })))
  await page.route('**/api/v1/admin/legal-entities', (r) => r.fulfill(json([{ id: 'e1', code: 'KSA', legalName: 'Acme KSA', countryCode: 'SA', baseCurrency: 'SAR', status: 'Active' }])))
  await page.route('**/api/v1/erp/reconciliation/summary', (r) => r.fulfill(json({ open: 1, pendingAcceptance: 0, accepted: 0, resolved: 0, openByType: { AmountMismatch: 1 } })))
  await page.route('**/api/v1/erp/reconciliation/items**', (r) => r.fulfill(json([{
    id: 'x1', legalEntityId: 'e1', type: 'AmountMismatch', invoiceId: 'i1', invoiceNumber: 'INV-100-WITH-A-LONG-NUMBER-THAT-SHOULD-WRAP', erpReference: 'JB-1',
    vpmsValue: '115.00 SAR', erpValue: '999.00 SAR', status: 'Open', firstSeenAt: '2026-10-02T08:00:00Z', lastSeenAt: '2026-10-03T08:00:00Z', proposedBy: null,
    proposedReason: null, decidedBy: null, decidedAt: null, decisionReason: null,
  }])))
  await page.route('**/api/v1/erp/sync/summary', (r) => r.fulfill(json({ pending: 0, inFlight: 0, succeeded: 3, deadLetter: 1, resolved: 0 })))
  await page.route('**/api/v1/erp/sync?**', (r) => r.fulfill(json([])))
}

test('the reconciliation page mirrors in Arabic, keeps the second-person rule visible, and never scrolls sideways on a phone', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 740 })
  await staff(page)
  await page.goto('/erp/reconciliation')
  await expect(page.getByRole('heading', { name: 'Reconciliation', level: 1 })).toBeVisible()
  await expect(page.getByText('needs a second person to confirm')).toBeVisible()
  await page.getByTestId('language-select').selectOption('ar')
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl')
  await expect(page.getByRole('heading', { name: 'المطابقة', level: 1 })).toBeVisible()
  await expect(page.getByText('تأكيد من شخص ثانٍ')).toBeVisible()
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow).toBeLessThanOrEqual(0)
})

test('the sync monitor shows the counts and fits a phone screen in both directions', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 740 })
  await staff(page)
  await page.goto('/erp/sync')
  await expect(page.getByRole('heading', { name: 'Sync monitor', level: 1 })).toBeVisible()
  await expect(page.getByText('Need attention')).toBeVisible()
  await page.getByTestId('language-select').selectOption('ar')
  await expect(page.getByRole('heading', { name: 'متابعة الإرسال', level: 1 })).toBeVisible()
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow).toBeLessThanOrEqual(0)
})

test('the connection and mapping screen keeps the secret field a name, mirrors in Arabic and fits a phone', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 740 })
  await staff(page)
  const conn = {
    id: 'c1', kind: 'Connection', legalEntityId: null, versionNo: 1, status: 'Active', mapping: null, note: 'Initial', createdBy: 'u9', createdAt: '2026-10-01T08:00:00Z', decidedBy: null, decidedAt: null, decisionComment: null,
    connection: { provider: 'D365Finance', baseUrl: 'https://contoso.operations.dynamics.com', authorityTenantId: 'tenant-1', clientId: 'client-1', secretRef: 'erp-secret', entitySets: {}, pageSize: 200, timeoutSeconds: 60 },
  }
  await page.route('**/api/v1/admin/erp/connection', (r) => r.fulfill(json({ active: conn, pending: null })))
  await page.route('**/api/v1/admin/legal-entities/e1/erp-mapping', (r) => r.fulfill(json({ active: null, pending: null })))
  await page.route('**/api/v1/erp-changes**', (r) => r.fulfill(json([])))
  await page.goto('/erp')
  await expect(page.getByRole('heading', { name: 'ERP integration', level: 1 })).toBeVisible()
  await expect(page.getByLabel('Name of the secret in the vault')).toHaveValue('erp-secret')
  await expect(page.getByText('never the secret itself')).toBeVisible()
  await page.getByTestId('language-select').selectOption('ar')
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl')
  await expect(page.getByText('اسم السر في المخزن الآمن')).toBeVisible()
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow).toBeLessThanOrEqual(0)
})
