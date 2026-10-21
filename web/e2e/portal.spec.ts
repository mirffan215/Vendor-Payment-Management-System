import { expect, test, type Page } from '@playwright/test'

async function portalAnonymous(page: Page) {
  await page.route('**/api/v1/portal/session', (route) =>
    route.fulfill({ status: 401, contentType: 'application/problem+json', body: JSON.stringify({ status: 401 }) }),
  )
}

test('the portal sign-in screen is its own area and mirrors in Arabic', async ({ page }) => {
  await portalAnonymous(page)
  await page.goto('/portal')
  await expect(page).toHaveURL(/\/portal\/login$/)
  await expect(page.getByRole('heading', { name: 'Vendor sign-in' })).toBeVisible()

  const brand = page.locator('header strong')
  const width = page.viewportSize()!.width
  const ltr = await brand.boundingBox()
  expect(ltr!.x + ltr!.width / 2).toBeLessThan(width / 2)

  await page.getByTestId('language-select').selectOption('ar')
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl')
  await expect(page.getByRole('heading', { name: 'تسجيل دخول الموردين' })).toBeVisible()
  const rtl = await brand.boundingBox()
  expect(rtl!.x + rtl!.width / 2).toBeGreaterThan(width / 2)
})

test('an invalid invitation link gives one neutral message', async ({ page }) => {
  await portalAnonymous(page)
  await page.route('**/portal/auth/invite**', (route) =>
    route.fulfill({ status: 404, contentType: 'application/problem+json', body: JSON.stringify({ status: 404 }) }),
  )
  await page.goto('/portal/accept?tenant=acme&token=nonsense')
  await expect(page.getByRole('alert')).toContainText('not valid or has expired')
})

test('portal sign-in fits a phone screen without horizontal scrolling, in both directions', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 740 })
  await portalAnonymous(page)
  for (const lang of ['en', 'ar']) {
    await page.goto('/portal/login')
    await page.getByTestId('language-select').selectOption(lang)
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    expect(overflow).toBeLessThanOrEqual(0)
  }
})
