import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// The 2026-10-21 round (D-291 to D-296), end to end in a real browser: per-field permissions on the account page,
// "remember each browser's last method", Platform Mapping suggestions, number and date formats, and the audit log's
// Verify. Each test puts back what it changed.

async function accountKeyOf(page, accountName) {
  const accounts = await (await page.request.get('/api/account-progress')).json()
  const match = accounts.find(a => a.accountName === accountName)
  if (!match) throw new Error(`Synthetic account ${accountName} not found (Database/Test/02).`)
  return match.accountKey
}

test.describe('Per-field permissions (D-291, D-296)', () => {
  test('An Analyst changes the stage but sees Source of Record read-only, with the permission it needs', async ({ page }) => {
    await signInAs(page, 'TestUser.Analyst')
    const accountKey = await accountKeyOf(page, 'TestAccount04')
    try {
      await page.goto(`/accounts/${accountKey}`)
      await expect(page.locator('form button[type="submit"]')).toBeVisible()

      await expect(page.getByLabel('Blueprint Stage')).toBeEnabled()
      await expect(page.getByLabel('Account Type')).toBeEnabled()
      await expect(page.getByRole('combobox', { name: 'Application:', exact: true })).toBeEnabled()
      await expect(page.getByLabel('Source of Record')).toBeDisabled()
      await expect(page.getByText('Changing this needs the CuratePlatformMapping permission.')).toBeVisible()
    } finally {
      await page.request.delete(`/api/account-progress/${accountKey}/lock`)
    }
  })
})

test.describe('Remember each browser\'s last method (D-292)', () => {
  test('Admin turns it on beside the default sign-in method, and it is saved', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    const original = await (await page.request.get('/api/admin/identity-providers/default')).json()
    try {
      await page.goto('/admin/identity-providers')
      const box = page.getByLabel('Remember each browser\'s last method')
      await box.setChecked(!original.rememberLastSignInMethod)
      await page.locator('form.filter-row').getByRole('button', { name: 'Save' }).click()
      await expect(page.getByText('Saved. It applies the next time someone signs in.')).toBeVisible()

      await page.reload()
      await expect(page.getByLabel('Remember each browser\'s last method')).toBeChecked({ checked: !original.rememberLastSignInMethod })
    } finally {
      const restored = await page.request.put('/api/admin/identity-providers/default', {
        data: { providerType: original.providerType, rememberLastSignInMethod: original.rememberLastSignInMethod }
      })
      expect(restored.ok()).toBe(true)
    }
  })
})

test.describe('Platform Mapping suggestions (D-293)', () => {
  test('Admin uses the suggestion for an unmapped platform and saves it', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    // Database/Test/06 seeds WinDomain-E2E, unmapped.
    const platform = (await (await page.request.get('/api/admin/platform-mapping')).json()).find(r => r.platformId === 'WinDomain-E2E')
    if (!platform) throw new Error('WinDomain-E2E not found (Database/Test/06).')
    if (!platform.isUnmapped) await page.request.delete(`/api/admin/platform-mapping/${platform.platformKey}`)
    try {
      await page.goto('/admin/platform-mapping')
      await page.getByLabel('Search:').fill('WinDomain-E2E')
      const row = page.locator('tbody tr', { hasText: 'WinDomain-E2E' })
      await expect(row.getByText('Suggested: Domain Account / Active Directory (the name contains "windomain")')).toBeVisible()

      await row.getByRole('button', { name: 'Use the suggestion for WinDomain-E2E' }).click()
      await expect(row.getByLabel('Account type for WinDomain-E2E').locator('option:checked')).toHaveText('Domain Account')
      await expect(row.getByLabel('Source of record for WinDomain-E2E').locator('option:checked')).toHaveText('Active Directory')

      await row.getByRole('button', { name: 'Save' }).click()
      await expect(page.getByText(/Saved the mapping of WinDomain-E2E/)).toBeVisible()
      const saved = (await (await page.request.get('/api/admin/platform-mapping')).json()).find(r => r.platformId === 'WinDomain-E2E')
      expect(saved.accountTypeName).toBe('Domain Account')
      expect(saved.sourceOfRecord).toBe('Active Directory')
    } finally {
      await page.request.delete(`/api/admin/platform-mapping/${platform.platformKey}`)
    }
  })
})

test.describe('Number and date formats (D-294)', () => {
  test('Global Application Configuration shows an example in the chosen format', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/configuration')
    const input = page.locator('input[list="format-cultures"]')
    await expect(input).toHaveValue('en-US')
    await expect(page.getByText('Example: 1,234,567.5, 10/21/2026.')).toBeVisible()
    // Not saved: the server's format is process-wide, and other tests read emails and summaries in en-US.
    await input.fill('de-DE')
    await expect(page.getByText('Example: 1.234.567,5, 21.10.2026.')).toBeVisible()
  })

  test.describe('in a German browser', () => {
    test.use({ locale: 'de-DE' })

    test('the account page shows dates in the browser\'s format', async ({ page }) => {
      await signInAs(page, 'TestUser.Viewer')
      const accountKey = await accountKeyOf(page, 'TestAccount03')
      await page.goto(`/accounts/${accountKey}`)
      // The read-only view (a Viewer can't edit); the page's text stays English, the date is dd.mm.yyyy.
      await expect(page.locator('dd').filter({ hasText: /^\d{2}\.\d{2}\.\d{4}$/ }).first()).toBeVisible()
    })
  })
})

test.describe('Audit log seals (D-295)', () => {
  test('Admin runs Verify on the Audit Log page and sees the result', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/audit-log')
    await expect(page.getByRole('heading', { name: 'Seals' })).toBeVisible()
    await expect(page.getByText(/events? sealed\./)).toBeVisible()

    await page.getByRole('button', { name: 'Verify' }).click()
    // Other suites delete their own audit rows, so the test database's chain may be broken; either result is shown.
    await expect(page.getByText(/^(Intact: .*sealed events? checked\.|Broken: these problems were found in .* sealed events\.)$/)).toBeVisible({ timeout: 30000 })
    await expect(page.getByText(/Last check /)).toBeVisible()
  })
})
