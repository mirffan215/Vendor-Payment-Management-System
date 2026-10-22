import { expect, test, type Page } from '@playwright/test'

const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })

async function staff(page: Page) {
  await page.route('**/api/v1/session', (r) => r.fulfill(json({ userId: 'u', tenantId: 't', email: 'a@acme.com', displayName: 'Alice', locale: 'en', csrfToken: 'x' })))
  await page.route('**/api/v1/approvals/inbox', (r) => r.fulfill(json([{
    taskId: 't1', runId: 'r1', stepId: 'l1', subjectType: 'Invoice', subjectId: 'i1', subjectLabel: 'INV-100 - Al Amal Trading', legalEntityId: 'e1', amount: 115, currency: 'SAR',
    mode: 'AnyOne', approvalsSoFar: 0, approvers: 1, createdAt: '2026-10-02T08:00:00Z', dueAt: '2026-10-04T08:00:00Z', overdue: true, escalated: true, actingForId: 'u9', actingForName: 'Dana',
  }])))
  await page.route('**/api/v1/approvals/notices', (r) => r.fulfill(json([])))
  await page.route('**/api/v1/admin/legal-entities', (r) => r.fulfill(json([{ id: 'e1', code: 'KSA', legalName: 'Acme KSA', countryCode: 'SA', baseCurrency: 'SAR', status: 'Active' }])))
  await page.route('**/api/v1/admin/legal-entities/e1/workflow', (r) => r.fulfill(json({ active: null, pending: null })))
  await page.route('**/api/v1/admin/legal-entities/e1/approval-materiality', (r) => r.fulfill(json({
    active: { id: null, versionNo: 0, status: 'Default', config: { amountPercent: 0, amountAbsolute: 0, materialFields: ['total'] }, note: '' }, pending: null,
  })))
  await page.route('**/api/v1/approval-materiality/fields', (r) => r.fulfill(json({ money: ['total', 'taxTotal'], other: ['currency', 'vendor'] })))
  await page.route('**/api/v1/approval-materiality-changes**', (r) => r.fulfill(json([])))
  await page.route('**/api/v1/workflow-changes**', (r) => r.fulfill(json([])))
}

for (const [path, english, arabic] of [
  ['/approvals', 'Approvals', 'الموافقات'],
  ['/workflow', 'Approval workflow', 'مسار الموافقة'],
] as const) {
  test(`${path} mirrors in Arabic and never scrolls the page sideways on a phone`, async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 740 })
    await staff(page)
    await page.goto(path)
    await expect(page.getByRole('heading', { name: english, level: 1 })).toBeVisible()
    await page.getByTestId('language-select').selectOption('ar')
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl')
    await expect(page.getByRole('heading', { name: arabic, level: 1 })).toBeVisible()
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    expect(overflow).toBeLessThanOrEqual(0)
  })
}

test('the inbox shows deadline and delegation hints in Arabic', async ({ page }) => {
  await staff(page)
  await page.goto('/approvals')
  await page.getByTestId('language-select').selectOption('ar')
  await expect(page.getByText('متأخر')).toBeVisible()
  await expect(page.getByText('يمكنك البتّ في هذا نيابةً عن Dana.')).toBeVisible()
})
