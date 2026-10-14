import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// D-233: Admin > Account Classification and its columns on Reports >
// Discovered Accounts. Database/Test/05_BlueTrack_Test_DiscoveredAccountSeed.sql
// seeds the discovered account e2eadm.jsmith (domain e2e-classification.test)
// and its owner jsmith (Jane Smith) as read from AD for owners. The test turns
// classification on, adds a name rule on the page, sees the class and owner on
// the report, then removes the rule and puts the settings back.

const prefix = 'e2eadm.'

async function removeLeftoverRules(page) {
  const body = await (await page.request.get('/api/admin/account-classification')).json()
  for (const rule of body.rules.filter(r => r.value === prefix)) {
    await page.request.delete(`/api/admin/account-classification/rules/${rule.accountClassificationRuleKey}`)
  }
  return body.settings
}

test.describe('Account Classification', () => {
  test('Admin turns classification on, adds a name rule and sees the class and owner on Discovered Accounts', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    const original = await removeLeftoverRules(page)
    try {
      await page.goto('/admin/account-classification')
      await expect(page.getByRole('heading', { name: 'Account Classification' })).toBeVisible()
      await page.getByLabel('Classify discovered accounts').check()
      await page.getByRole('button', { name: 'Save Settings' }).click()
      await expect(page.getByText(/^Settings saved./)).toBeVisible()

      await page.getByRole('button', { name: '+ New Name Rule' }).click()
      await expect(page.getByRole('heading', { name: 'New Classification Rule' })).toBeVisible()
      await page.getByRole('combobox', { name: 'Operator' }).selectOption({ label: 'starts with' })
      await page.getByLabel('Value').fill(prefix)
      await page.getByRole('combobox', { name: 'Class', exact: true }).selectOption({ label: 'Admin' })
      await page.getByRole('button', { name: 'Save' }).click()

      await expect(page.getByRole('heading', { name: 'Account Classification' })).toBeVisible()
      await expect(page.getByRole('link', { name: `sAMAccountName starts with "${prefix}"` })).toBeVisible()

      await page.goto('/reports/discovered-accounts')
      await expect(page.getByRole('heading', { name: 'Discovered Accounts' })).toBeVisible()
      await page.getByRole('combobox', { name: 'Class:' }).selectOption({ label: 'Admin' })
      const row = page.getByRole('row').filter({ hasText: 'e2eadm.jsmith' })
      await expect(row).toContainText('Admin')
      await expect(row).toContainText('jsmith (Jane Smith)')
      await expect(row).toContainText(`sAMAccountName starts with "${prefix}"`)
      await expect(row).toContainText('Owner by name')
    } finally {
      await removeLeftoverRules(page)
      const restored = await page.request.put('/api/admin/account-classification/settings', { data: original })
      expect(restored.ok()).toBe(true)
    }
  })

  test('Viewer has no Account Classification section and the API refuses it', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/admin')
    await expect(page.getByRole('link', { name: 'Account Classification' })).toHaveCount(0)
    expect((await page.request.get('/api/admin/account-classification')).status()).toBe(403)
  })
})
