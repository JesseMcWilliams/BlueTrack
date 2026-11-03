import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// D-298: an archive Progress Rule, made and tested on the rule page, and the Archived count on the KPI Summary. The
// rule is saved disabled and deleted again, so the nightly load never uses it.

test.describe('Archive rules (D-298)', () => {
  test('Admin makes an archive rule with no stages, Test counts the account it would archive, and the list shows it', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    const name = `E2E archive ${Date.now()}`
    try {
      await page.goto('/admin/progress-rules/new')
      await page.getByRole('combobox', { name: 'The rule', exact: true }).selectOption('Archive')
      await expect(page.getByRole('combobox', { name: 'Move accounts from', exact: true })).toHaveCount(0)
      await page.getByRole('textbox', { name: 'Name', exact: true }).fill(name)
      await page.getByRole('checkbox', { name: 'Enabled (the nightly load uses it)' }).uncheck()

      await page.getByRole('combobox', { name: 'Field of condition 1', exact: true }).selectOption('AccountName')
      await page.getByRole('combobox', { name: 'Comparison of condition 1', exact: true }).selectOption('Equals')
      await page.getByRole('textbox', { name: 'Value of condition 1', exact: true }).fill('TestAccount01')
      await page.getByRole('button', { name: 'Test', exact: true }).click()
      await expect(page.locator('.preview')).toContainText('1 account would be archived now.')

      await page.getByRole('button', { name: 'Save', exact: true }).click()
      await expect(page).toHaveURL(/\/admin\/progress-rules$/)
      const row = page.getByRole('row').filter({ hasText: name })
      await expect(row).toContainText('to Archived (any stage)')
      await expect(row).toContainText('Account name is TestAccount01')
    } finally {
      const rules = await (await page.request.get('/api/admin/progress-rules')).json()
      for (const rule of rules.filter(r => r.ruleName === name)) {
        expect((await page.request.delete(`/api/admin/progress-rules/${rule.progressRuleKey}`)).ok()).toBe(true)
      }
    }
  })

  test('The KPI Summary counts Archived accounts on their own and defines them', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/reports/kpi-summary')
    await expect(page.getByText('Archived (out of scope):')).toBeVisible()
    await expect(page.getByRole('term').filter({ hasText: 'Archived' })).toBeVisible()
  })
})
