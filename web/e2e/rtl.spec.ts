import { expect, test, type Page } from '@playwright/test'

async function anonymous(page: Page) {
  await page.route('**/api/v1/session', (route) =>
    route.fulfill({ status: 401, contentType: 'application/problem+json', body: JSON.stringify({ status: 401 }) }),
  )
}

test('anonymous visitors land on the sign-in page', async ({ page }) => {
  await anonymous(page)
  await page.goto('/')
  await expect(page).toHaveURL(/\/login$/)
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
  await expect(page.locator('html')).toHaveAttribute('dir', 'ltr')
})

test('Arabic mirrors the whole layout', async ({ page }) => {
  await anonymous(page)
  await page.goto('/login')

  const brand = page.locator('header strong')
  const ltrBox = await brand.boundingBox()
  const viewport = page.viewportSize()!
  expect(ltrBox!.x + ltrBox!.width / 2).toBeLessThan(viewport.width / 2)

  await page.getByTestId('language-select').selectOption('ar')

  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl')
  await expect(page.locator('html')).toHaveAttribute('lang', 'ar')
  await expect(page.getByRole('heading', { name: 'تسجيل الدخول' })).toBeVisible()

  const rtlBox = await brand.boundingBox()
  expect(rtlBox!.x + rtlBox!.width / 2).toBeGreaterThan(viewport.width / 2)
})

test('the language choice survives a reload', async ({ page }) => {
  await anonymous(page)
  await page.goto('/login')
  await page.getByTestId('language-select').selectOption('ar')
  await page.reload()
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl')
})

test('works on a phone-sized screen without horizontal scrolling', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 740 })
  await anonymous(page)
  await page.goto('/login')
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
  expect(overflow).toBeLessThanOrEqual(0)
})
