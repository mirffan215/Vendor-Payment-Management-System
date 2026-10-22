import { expect, test, type Page } from '@playwright/test'

const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })

async function staff(page: Page) {
  await page.route('**/api/v1/session', (r) => r.fulfill(json({ userId: 'u', tenantId: 't', email: 'a@acme.com', displayName: 'Alice', locale: 'en', csrfToken: 'x' })))
  await page.route('**/api/v1/invoices?**', (r) => r.fulfill(json([
    { id: 'i1', legalEntityId: 'e1', vendorId: 'v1', vendorName: 'Al Amal Trading', invoiceNumber: 'INV-100', invoiceDate: '2026-10-01', currencyCode: 'SAR', total: 115, source: 'Email', status: 'Exception', createdAt: '2026-10-02T08:00:00Z' },
  ])))
  await page.route('**/api/v1/admin/legal-entities', (r) => r.fulfill(json([{ id: 'e1', code: 'KSA', legalName: 'Acme KSA', countryCode: 'SA', baseCurrency: 'SAR', status: 'Active' }])))
  await page.route('**/api/v1/vendors?**', (r) => r.fulfill(json([])))
}

test('the invoice list mirrors in Arabic and never scrolls the page sideways on a phone', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 740 })
  await staff(page)
  await page.goto('/invoices')
  await expect(page.getByRole('heading', { name: 'Invoices' })).toBeVisible()
  await expect(page.locator('td', { hasText: 'Needs attention' })).toHaveCount(1)

  await page.getByTestId('language-select').selectOption('ar')
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl')
  await expect(page.getByRole('heading', { name: 'الفواتير' })).toBeVisible()
  await expect(page.locator('td', { hasText: 'تحتاج انتباهاً' })).toHaveCount(1)
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow).toBeLessThanOrEqual(0)
})

test('the new-invoice form fits a phone screen in both directions', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 740 })
  await staff(page)
  for (const lang of ['en', 'ar']) {
    await page.goto('/invoices')
    await page.getByTestId('language-select').selectOption(lang)
    await page.getByRole('button', { name: lang === 'en' ? 'New invoice' : 'فاتورة جديدة' }).click()
    await expect(page.locator('form table')).toBeVisible()
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    expect(overflow).toBeLessThanOrEqual(0)
  }
})
