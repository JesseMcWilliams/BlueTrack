import { test, expect } from '@playwright/test'

// Read-only checks of a deployed site (playwright.live.config.js). Each test signs in with Windows first.

test.beforeEach(async ({ page }) => {
  await page.goto('/login/windows')
  await expect(page.locator('nav.top-nav')).toBeVisible({ timeout: 30000 })
})

test('signs in with Windows and About shows the version the API reports', async ({ page }) => {
  // Through the browser, which trusts the site's certificate as a user's browser does (Node may not know its CA).
  const about = await page.evaluate(async () => (await fetch('/BlueTrack/api/about')).json())
  expect(about.version).toBeTruthy()
  await page.locator('button.user-menu__button').click()
  await page.getByRole('button', { name: 'About' }).click()
  await expect(page.getByRole('dialog', { name: 'About BlueTrack' })).toContainText(`Version ${about.version}`)
})

test('Global Application Configuration has the Number and date format, with an example', async ({ page }) => {
  await page.goto('/admin/configuration')
  const input = page.locator('input[list="format-cultures"]')
  await expect(input).toBeVisible()
  await expect(page.locator('small', { hasText: /Example: .+, .+\./ })).toBeVisible()
})

test('Identity Providers has Remember each browser\'s last method', async ({ page }) => {
  await page.goto('/admin/identity-providers')
  await expect(page.getByLabel('Remember each browser\'s last method')).toBeVisible()
})

test('Platform Mapping lists the platforms', async ({ page }) => {
  await page.goto('/admin/platform-mapping')
  await expect(page.getByRole('heading', { name: 'Platform Mapping' })).toBeVisible()
  await expect(page.locator('tbody tr').first()).toBeVisible()
})

test('dates on a report are in the browser\'s format', async ({ page }) => {
  await page.goto('/reports/cpm-health')
  const rows = page.locator('tbody tr')
  await expect(page.getByRole('heading', { name: 'CPM Health' })).toBeVisible()
  if (await rows.count() > 0) {
    await expect(rows.first()).toContainText(/\d{2}\/\d{2}\/\d{4}/)
  }
  await expect(page.getByText(/\d{4}-\d{2}-\d{2}/)).toHaveCount(0)
})

test('the Audit Log shows the seals, and Verify gives a result', async ({ page }) => {
  await page.goto('/admin/audit-log')
  await expect(page.getByRole('heading', { name: 'Seals' })).toBeVisible()
  await expect(page.getByText(/events? sealed\./)).toBeVisible()
  await page.getByRole('button', { name: 'Verify' }).click()
  await expect(page.getByText(/^(Intact: .*sealed events? checked\.|Broken: these problems were found in .* sealed events\.)$/)).toBeVisible({ timeout: 120000 })
})
