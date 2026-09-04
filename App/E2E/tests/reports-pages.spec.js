import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// Layer 4: the 3 Reports sub-pages (D-56). Overdue/At-Risk and
// Stage/Status Summary have no [Authorize(Policy = ...)] on their API
// endpoints (any authenticated user can view), so ReportsHub.vue shows
// both links unconditionally -- only Reconciliation Review is gated
// (ConfirmReconciliation), confirmed by reading ReportsController.cs and
// ReportsHub.vue's own comment before writing these.

test.describe('Reports Hub navigation is gated per permission', () => {
  test('Admin sees all 3 report links', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/reports')

    const nav = page.locator('nav.reports-subnav')
    await expect(nav.getByRole('link', { name: 'Overdue / At-Risk' })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Stage/Status Summary' })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Reconciliation Review' })).toBeVisible()
  })

  test('Viewer does not see Reconciliation Review, which needs ConfirmReconciliation', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/reports')

    const nav = page.locator('nav.reports-subnav')
    await expect(nav.getByRole('link', { name: 'Overdue / At-Risk' })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Stage/Status Summary' })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Reconciliation Review' })).toHaveCount(0)
  })
})

test.describe('Overdue / At-Risk Worklist', () => {
  test('Viewer can load the report and navigate into an account from a row', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/reports/overdue')

    await expect(page.getByRole('heading', { name: 'Overdue / At-Risk Worklist' })).toBeVisible()
    await expect(page.getByText(/^Could not load accounts:/)).toHaveCount(0)

    const firstRow = page.locator('tbody tr').first()
    if (await firstRow.count() > 0) {
      await firstRow.click()
      await expect(page).toHaveURL(/\/accounts\/\d+$/)
    }
  })
})

test.describe('Stage/Status Funnel Summary', () => {
  test('Viewer can load the report and see the known Onboarded to Vault stage row', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/reports/stage-status-summary')

    await expect(page.getByRole('heading', { name: 'Stage/Status Funnel Summary' })).toBeVisible()
    await expect(page.getByText(/^Could not load summary:/)).toHaveCount(0)
    await expect(page.locator('tbody tr', { hasText: 'Onboarded to Vault' })).toBeVisible()
  })
})

test.describe('Reconciliation Review Queue', () => {
  test('Admin (who holds ConfirmReconciliation) can load the queue', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/reports/reconciliation-review')

    await expect(page.getByRole('heading', { name: 'Reconciliation Review Queue' })).toBeVisible()
    await expect(page.getByText(/^Could not load queue:/)).toHaveCount(0)
  })

  test('Viewer (who does not hold ConfirmReconciliation) is denied with a plain error', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/reports/reconciliation-review')

    await expect(page.getByText(/Could not load queue:.*403/)).toBeVisible()
  })
})
