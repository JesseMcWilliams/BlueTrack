import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// Layer 4 (Design_Testing_Strategy.md): D-121 promoted Targets/Access
// Groups out of the Admin hub to their own top-level nav entries and
// granted Analyst full parity with Admin on ManageTargets/
// ManageAccessGroups -- this is the first E2E coverage for either page.
// Confirmed by reading App.vue/AdminHub.vue/Targets.vue/AccessGroups.vue
// before writing these: the top nav gates each link on its own permission
// (rights.hasPermission), and both pages now have real filter dropdowns
// plus the shared "Showing N of M total" count summary
// (FilterCountSummary/useTotalCount) that didn't exist on any page before
// this work.

test.describe('Targets/Access Groups are top-level nav entries, gated per permission', () => {
  test('Analyst sees both Targets and Access Groups in the top nav (full parity with Admin)', async ({ page }) => {
    await signInAs(page, 'TestUser.Analyst')
    await page.goto('/')

    const topNav = page.locator('nav.top-nav')
    await expect(topNav.getByRole('link', { name: 'Targets' })).toBeVisible()
    await expect(topNav.getByRole('link', { name: 'Access Groups' })).toBeVisible()
  })

  test('Viewer sees neither -- Viewer holds neither ManageTargets nor ManageAccessGroups', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/')

    const topNav = page.locator('nav.top-nav')
    await expect(topNav.getByRole('link', { name: 'Targets' })).toHaveCount(0)
    await expect(topNav.getByRole('link', { name: 'Access Groups' })).toHaveCount(0)
  })

  test('Targets/Access Groups no longer appear in the Admin hub sidebar (moved out, not duplicated)', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin')

    const adminNav = page.locator('nav.admin-subnav')
    await expect(adminNav.getByRole('link', { name: 'Targets', exact: true })).toHaveCount(0)
    await expect(adminNav.getByRole('link', { name: 'Access Groups', exact: true })).toHaveCount(0)
    // The two admin-only sub-pages stay put, unmoved.
    await expect(adminNav.getByRole('link', { name: 'Target Match Review' })).toBeVisible()
    await expect(adminNav.getByRole('link', { name: 'Import Mapping Profiles' })).toBeVisible()
  })
})

test.describe('Targets page', () => {
  // D-124 Phase 4: Add/Edit moved off this page's own inline form onto
  // TargetEdit.vue's routed pages (/targets/new, /targets/:targetKey) --
  // confirms the whole navigate-fill-save-return round trip, not just that
  // the form itself still submits.
  test('Analyst can add a Target, see it in the filtered list, and the count summary reflects it', async ({ page }) => {
    await signInAs(page, 'TestUser.Analyst')
    await page.goto('/targets')

    await expect(page.getByRole('heading', { name: 'Targets' })).toBeVisible()
    await expect(page.getByText('Loading...')).toHaveCount(0)

    const totalBefore = await readTotalCount(page)

    const targetName = `E2E Test Target ${Date.now()}`
    await page.getByRole('link', { name: '+ New Target' }).click()
    await expect(page).toHaveURL(/\/targets\/new$/)
    await expect(page.getByRole('heading', { name: 'New Target' })).toBeVisible()
    await page.getByLabel('Name:').fill(targetName)
    await page.getByLabel('Type:').selectOption('Server')
    await page.getByLabel('Risk Score (0-1000):').fill('250')
    await page.locator('form button[type="submit"]').click()
    await expect(page).toHaveURL(/\/targets$/)

    const row = page.locator('tbody tr', { hasText: targetName })
    await expect(row).toBeVisible()

    // Count summary tracks the grand total, not just the filtered view (D-121).
    await expect(page.getByText(/^Showing \d+ of \d+ total$/)).toBeVisible()
    const totalAfter = await readTotalCount(page)
    expect(totalAfter).toBe(totalBefore + 1)

    // Filtering by a type that excludes the new row narrows the visible
    // rows without changing the grand total (X-Total-Count ignores filters).
    await page.getByLabel('Type:').selectOption('Database')
    await expect(page.locator('tbody tr', { hasText: targetName })).toHaveCount(0)
    expect(await readTotalCount(page)).toBe(totalAfter)
    await page.getByLabel('Type:').selectOption('')

    await expect(page.locator('tbody tr', { hasText: targetName })).toBeVisible()
    await row.getByRole('link', { name: 'Edit' }).click()
    await expect(page.getByRole('heading', { name: 'Edit Target' })).toBeVisible()
    // Confirms the edit page actually pre-fetched and pre-filled this
    // specific row (a fresh navigation, not the already-loaded list item).
    await expect(page.getByLabel('Name:')).toHaveValue(targetName)
    const updatedName = `${targetName} (Updated)`
    await page.getByLabel('Name:').fill(updatedName)
    await page.locator('form button[type="submit"]').click()
    await expect(page).toHaveURL(/\/targets$/)
    await expect(page.locator('tbody tr', { hasText: updatedName })).toBeVisible()

    await page.locator('tbody tr', { hasText: updatedName }).getByRole('button', { name: 'Delete' }).click()
    await expect(page.locator('tbody tr', { hasText: updatedName })).toHaveCount(0)
  })
})

test.describe('Access Groups page', () => {
  // D-124 Phase 4: Add/Edit moved off this page's own inline form onto
  // AccessGroupEdit.vue's routed pages (/access-groups/new,
  // /access-groups/:accessGroupKey).
  test('Analyst can add an Access Group with SOR Type/SOR Address and see Discovery Source in the table', async ({ page }) => {
    await signInAs(page, 'TestUser.Analyst')
    await page.goto('/access-groups')

    await expect(page.getByRole('heading', { name: 'Access Groups' })).toBeVisible()
    await expect(page.getByText('Loading...')).toHaveCount(0)

    const groupName = `E2E Test Group ${Date.now()}`
    await page.getByRole('link', { name: '+ New Access Group' }).click()
    await expect(page).toHaveURL(/\/access-groups\/new$/)
    await expect(page.getByRole('heading', { name: 'New Access Group' })).toBeVisible()
    await page.getByLabel('Name:').fill(groupName)
    await page.getByLabel('Identifier (e.g. AD SID/DN):').fill(`CN=${groupName}`)
    await page.getByLabel('SOR Type:').selectOption('Domain')
    await page.getByLabel('SOR Address:').fill('e2e.example.com')
    await page.getByLabel('Base Risk Score (0-1000):').fill('150')
    await page.getByLabel('Discovery Source:').fill('E2E Test')
    await page.locator('form button[type="submit"]').click()
    await expect(page).toHaveURL(/\/access-groups$/)

    const row = page.locator('tbody tr', { hasText: groupName })
    await expect(row).toBeVisible()
    await expect(row).toContainText('Domain')
    await expect(row).toContainText('e2e.example.com')
    await expect(row).toContainText('E2E Test')

    await row.getByRole('link', { name: 'Edit' }).click()
    await expect(page.getByRole('heading', { name: 'Edit Access Group' })).toBeVisible()
    await expect(page.getByLabel('SOR Address:')).toHaveValue('e2e.example.com')
    await page.getByRole('button', { name: 'Cancel' }).click()
    await expect(page).toHaveURL(/\/access-groups$/)

    await page.locator('tbody tr', { hasText: groupName }).getByRole('button', { name: 'Delete' }).click()
    await expect(page.locator('tbody tr', { hasText: groupName })).toHaveCount(0)
  })
})

/** Reads the "Showing N of M total" count summary's M -- assumes it's already visible. */
async function readTotalCount(page) {
  const text = await page.getByText(/^Showing \d+ of \d+ total$/).textContent()
  return Number(text.match(/of (\d+) total/)[1])
}
