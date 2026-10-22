import { expect, test, type Page } from '@playwright/test'

const json = (body: unknown) => ({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })

/** Every signed-in staff screen, with the ids dynamic routes need. */
const ROUTES = [
  '/', '/vendors', '/vendors/v1', '/invoices', '/invoices/i1', '/approvals', '/approvals/t1', '/payments', '/payments/proposals/p1', '/erp', '/erp/sync', '/erp/reconciliation',
  '/reports', '/notifications', '/admin/notifications', '/delegations', '/workflow', '/exceptions', '/intake', '/verification', '/bank-changes', '/admin', '/audit',
]

async function staffInArabic(page: Page) {
  await page.route('**/api/**', (r) => {
    const url = new URL(r.request().url())
    // Records that do not exist (the ids in ROUTES) answer like the real API; lists are empty.
    if (/\/(v1|i1|t1|p1)(\/|$)/.test(url.pathname)) return r.fulfill({ status: 404, contentType: 'application/problem+json', body: JSON.stringify({ status: 404, errorCode: 'not_found' }) })
    if (url.pathname.endsWith('/audit/events')) return r.fulfill(json({ items: [], nextAfterSequence: null }))
    return r.fulfill(json([]))
  })
  await page.route('**/api/v1/session', (r) => r.fulfill(json({ userId: 'u', tenantId: 't', email: 'a@acme.com', displayName: 'Alice', locale: 'ar', calendar: 'gregorian', csrfToken: 'x' })))
}

/** A translation key shown instead of text looks like "area.name" or "area.group.name". */
const RAW_KEY = /\b(?:nav|reports|notifications|notificationAdmin|audit|erp|invoices|payments|approvals|vendors|errors|common|admin|workflow|exceptions|intake|verification|bankChanges|delegations)\.[A-Za-z]+(?:\.[A-Za-z]+)*\b/

for (const route of ROUTES) {
  test(`Arabic ${route}: right to left, no sideways scroll on a phone, no untranslated keys`, async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 740 })
    await staffInArabic(page)
    await page.goto(route)
    await expect(page.locator('html')).toHaveAttribute('dir', 'rtl')
    await expect(page.locator('html')).toHaveAttribute('lang', 'ar')
    await expect(page.locator('main, #main').first()).toBeVisible()
    await page.waitForLoadState('networkidle')
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    expect(overflow, `overflow on ${route}`).toBeLessThanOrEqual(0)
    const text = await page.locator('body').innerText()
    expect(text.match(RAW_KEY)?.[0], `raw key on ${route}`).toBeUndefined()
  })
}
