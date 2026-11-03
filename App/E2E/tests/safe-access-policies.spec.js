import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// D-300/D-301: an Admin makes a Safe role and a policy for one seeded Safe and checks it; the report shows the result,
// and an Approver marks one of its accounts reviewed and clears it again. Everything made here is deleted at the end.

async function accountKeyOf(page, accountName) {
  const accounts = await (await page.request.get('/api/account-progress')).json()
  const match = accounts.find(a => a.accountName === accountName)
  if (!match) throw new Error(`Synthetic account ${accountName} not found (Database/Test/02).`)
  return match.accountKey
}

test.describe('Safe access policies (D-300, D-301)', () => {
  test('Admin makes a role and a policy, checks it, and the report shows the Safe passing', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    const token = `E2E${Date.now()}`
    try {
      await page.goto('/admin/safe-access-policies')
      await page.getByRole('button', { name: 'New Safe role' }).click()
      await page.getByRole('textbox', { name: 'Name', exact: true }).fill(`${token} role`)
      await page.getByRole('checkbox', { name: 'ListAccounts', exact: true }).check()
      await page.getByRole('button', { name: 'Save', exact: true }).click()
      await expect(page).toHaveURL(/\/admin\/safe-access-policies$/)

      await page.getByRole('button', { name: 'New policy' }).click()
      await page.getByRole('textbox', { name: 'Name', exact: true }).fill(`${token} policy`)
      await page.getByRole('textbox', { name: 'Safes it applies to' }).fill('TestSafe01')
      await page.getByRole('spinbutton', { name: 'Order' }).fill('0')
      // The form starts with a Required rule; this one only forbids a group no Safe has.
      await page.getByRole('button', { name: 'Remove rule 1' }).click()
      await page.getByRole('button', { name: 'Add a Forbidden member' }).click()
      await page.getByRole('textbox', { name: 'Member names of rule 1' }).fill(`${token}*`)
      await page.getByRole('button', { name: 'Save', exact: true }).click()
      await expect(page).toHaveURL(/\/admin\/safe-access-policies$/)
      await expect(page.getByRole('row').filter({ hasText: `${token} policy` })).toContainText(`Forbidden: ${token}*`)

      await page.getByRole('button', { name: 'Check now' }).click()
      await expect(page.getByText('Every Safe was checked.')).toBeVisible()

      await page.goto('/reports/safe-access')
      const row = page.getByRole('row').filter({ has: page.getByRole('cell', { name: /^TestSafe01 / }) })
      await expect(row).toContainText(`${token} policy`)
      await expect(row).toContainText('Passed')
    } finally {
      const policies = await (await page.request.get('/api/admin/safe-access/policies')).json()
      for (const p of policies.filter(x => x.policyName.startsWith(token))) {
        expect((await page.request.delete(`/api/admin/safe-access/policies/${p.safeAccessPolicyKey}`)).ok()).toBe(true)
      }
      const roles = await (await page.request.get('/api/admin/safe-access/roles')).json()
      for (const r of roles.filter(x => x.roleName.startsWith(token))) {
        expect((await page.request.delete(`/api/admin/safe-access/roles/${r.safeRoleKey}`)).ok()).toBe(true)
      }
      expect((await page.request.post('/api/admin/safe-access/check')).ok()).toBe(true)
    }
  })

  test('An Approver marks an account\'s compliance reviewed, then clears it', async ({ page }) => {
    await signInAs(page, 'TestUser.Approver')
    const accountKey = await accountKeyOf(page, 'TestAccount01')
    try {
      await page.goto(`/accounts/${accountKey}`)
      const section = page.locator('section.compliance')
      await expect(section).toContainText('Compliance not reviewed.')
      await section.getByRole('textbox', { name: 'Note (optional)' }).fill('E2E review')
      await section.getByRole('button', { name: 'Mark reviewed' }).click()
      await expect(section).toContainText('Compliance reviewed by')
      await expect(section).toContainText('Note: E2E review')
      await section.getByRole('button', { name: 'Clear review' }).click()
      await expect(section).toContainText('Compliance not reviewed.')
    } finally {
      await page.request.delete(`/api/account-progress/${accountKey}/compliance-review`)
      await page.request.delete(`/api/account-progress/${accountKey}/lock`)
    }
  })
})
