import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'
import { confirmDelete } from './confirmDialog.js'

// D-232: resolving a member on Reports > Unresolved Entitlement Members.
// Database/Test/04_BlueTrack_Test_UnresolvedEntitlementSeed.sql seeds one
// unresolved User member (ID e2e-0000-unresolved-guid, name E2E Unresolved
// Member) on TestSafe_Entitlements, and the Privilege Cloud user
// e2e.resolve.target. The test links one to the other and undoes the link, so
// it ends as it began. (Pre-filling the name match is covered by Vitest.)

const memberId = 'e2e-0000-unresolved-guid'

async function undoIfResolved(page) {
  // A failed earlier run may have left it resolved by hand: undo that first.
  const response = await page.request.post('/api/reports/unresolved-entitlement-members/unresolve', {
    data: { memberType: 'User', sourceMemberId: memberId }
  })
  expect([204, 404]).toContain(response.status())
}

test.describe('Unresolved Entitlement Members', () => {
  test('Approver links a member to a user, sees how under Show resolved, then undoes it', async ({ page }) => {
    await signInAs(page, 'TestUser.Approver')
    await undoIfResolved(page)
    await page.goto('/reports/unresolved-entitlement-members')

    await expect(page.getByRole('heading', { name: 'Unresolved Entitlement Members' })).toBeVisible()
    const row = page.getByRole('row').filter({ hasText: memberId })
    await expect(row).toContainText('E2E Unresolved Member')
    await expect(row).toContainText('Unresolved')

    await row.getByRole('button', { name: 'Resolve E2E Unresolved Member on TestSafe_Entitlements' }).click()
    await expect(page.getByRole('heading', { name: 'Resolve E2E Unresolved Member' })).toBeVisible()
    const search = page.getByRole('combobox', { name: 'User' })
    await search.fill('e2e.resolve')
    await page.getByRole('option', { name: /e2e\.resolve\.target/ }).click()
    await expect(search).toHaveValue('e2e.resolve.target')
    await page.getByRole('button', { name: 'Save' }).click()

    await expect(page.getByRole('heading', { name: 'Resolve E2E Unresolved Member' })).toHaveCount(0)
    await expect(page.getByRole('row').filter({ hasText: memberId })).toHaveCount(0)

    await page.getByLabel('Show resolved').check()
    const resolved = page.getByRole('row').filter({ hasText: memberId })
    await expect(resolved).toContainText(/Linked by .+: e2e\.resolve\.target/)

    await resolved.getByRole('button', { name: 'Undo the resolution of E2E Unresolved Member on TestSafe_Entitlements' }).click()
    await confirmDelete(page, 'Undo')
    await expect(page.getByRole('row').filter({ hasText: memberId })).not.toContainText('Linked by')
    await expect(page.getByRole('row').filter({ hasText: memberId })).toContainText('Unresolved')
  })

  test('Viewer sees the report but no Resolve button', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/reports/unresolved-entitlement-members')

    await expect(page.getByRole('heading', { name: 'Unresolved Entitlement Members' })).toBeVisible()
    await expect(page.getByRole('button', { name: /^Resolve / })).toHaveCount(0)
  })
})
