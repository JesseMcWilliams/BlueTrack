import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// D-99/D-100: Dashboard.vue and Login.vue were both literal placeholders
// until now -- Dashboard reuses existing Reports/Risk Exceptions endpoints
// as "at a glance" summary cards; Login lists the enabled identity
// providers from the real, public GET /api/auth/providers.

test.describe('Dashboard', () => {
  test('Viewer sees the stage funnel, overdue-at-risk, and overdue-review cards, but not the ApproveExceptions-gated card', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/')

    await expect(page.getByText('Loading...')).toHaveCount(0)
    await expect(page.getByRole('heading', { name: 'Accounts by Stage' })).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Overdue / At-Risk Accounts' })).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Risk Exceptions Needing Attention' })).toBeVisible()
    await expect(page.getByText(/exception\(s\) past their review date/)).toBeVisible()
    await expect(page.getByText(/active exception\(s\) awaiting approval/)).toHaveCount(0)
  })

  test('Approver (who holds ApproveExceptions) also sees the active-exceptions card', async ({ page }) => {
    await signInAs(page, 'TestUser.Approver')
    await page.goto('/')

    await expect(page.getByText('Loading...')).toHaveCount(0)
    await expect(page.getByText(/active exception\(s\) awaiting approval/)).toBeVisible()
    await page.getByRole('link', { name: 'View worklist' }).last().click()
    await expect(page).toHaveURL(/\/exceptions\/approvals$/)
  })
})

test.describe('Login', () => {
  test('lists the enabled identity providers from the real API, not a stub message', async ({ page }) => {
    // Deliberately not signed in first -- Login.vue's own content depends
    // only on the public GET /api/auth/providers, not on auth state.
    await page.goto('/login')

    await expect(page.getByText('Loading...')).toHaveCount(0)
    await expect(page.getByText('Provider redirect logic not yet implemented')).toHaveCount(0)
    await expect(page.getByText(/Windows Integrated.*signs in automatically/)).toBeVisible()
    await expect(page.getByText(/Dev Fake Auth.*signs in automatically/)).toBeVisible()
  })
})
