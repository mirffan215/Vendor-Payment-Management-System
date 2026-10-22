import { expect, test, type Page } from '@playwright/test'

const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })

async function staff(page: Page) {
  await page.route('**/api/v1/session', (r) => r.fulfill(json({ userId: 'u', tenantId: 't', email: 'a@acme.com', displayName: 'Alice', locale: 'en', csrfToken: 'x' })))
  await page.route('**/api/v1/payments/payable**', (r) => r.fulfill(json([{
    invoiceId: 'i1', legalEntityId: 'e1', vendorId: 'v1', vendorName: 'Al Amal Trading', invoiceNumber: 'INV-100', invoiceDate: '2026-10-01', dueDate: '2026-11-01', currencyCode: 'SAR',
    total: 115, approvedAt: '2026-10-05T08:00:00Z', hasOpenRequest: false, blockers: ['bank_change_pending'],
  }])))
  await page.route('**/api/v1/payments/requests**', (r) => r.fulfill(json([])))
  await page.route('**/api/v1/payments/proposals?**', (r) => r.fulfill(json([{
    id: 'p1', legalEntityId: 'e1', proposalNo: 'PP-2026-000001', vendorId: 'v1', vendorName: 'Al Amal Trading', dueDate: '2026-11-01', currencyCode: 'SAR', totalAmount: 115,
    status: 'Approved', statusReason: null, createdBy: 'u1', createdAt: '2026-10-06T09:00:00Z', submittedBy: null, approvedBy: null, approvedAt: null, approvalComment: null,
    releasedBy: null, releasedAt: null, items: [],
  }])))
}

test('the payments page mirrors in Arabic, keeps the no-transfer notice, and never scrolls sideways on a phone', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 740 })
  await staff(page)
  await page.goto('/payments')
  await expect(page.getByRole('heading', { name: 'Payments', level: 1 })).toBeVisible()
  await expect(page.getByText('never sends money to a bank')).toBeVisible()
  await page.getByTestId('language-select').selectOption('ar')
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl')
  await expect(page.getByRole('heading', { name: 'المدفوعات', level: 1 })).toBeVisible()
  await expect(page.getByText('تغيير بنكي بانتظار القرار.')).toBeVisible()
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow).toBeLessThanOrEqual(0)
})
