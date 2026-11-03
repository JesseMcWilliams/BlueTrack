import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// Layer 4 (Design_Testing-Strategy.md): confirms the DevFakeAuth test
// matrix (Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql) is
// reachable end to end -- real browser, real running API + built SPA,
// real BlueTrackTest database.
//
// D-304: the approval worklist needs ApproveExceptions at the router
// (routePermissions.js), so a Viewer opening it is sent to Access Denied
// instead of seeing the API's 403 inside the page.

test.describe('MyProfile reflects the signed-in DevFakeAuth role', () => {
  test('Approver sees the Approver role and ApproveExceptions permission', async ({ page }) => {
    await signInAs(page, 'TestUser.Approver')

    await page.goto('/profile')

    // D-114: permissions moved out of this <dl> into their own <table>
    // (one row per permission name) -- Role(s) is still in the <dl>.
    await expect(page.getByText('Role(s)')).toBeVisible()
    const dl = page.locator('dl')
    await expect(dl).toContainText('Approver')
    await expect(page.locator('table')).toContainText('ApproveExceptions')
  })

  test('Viewer sees the Viewer role without ApproveExceptions', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')

    await page.goto('/profile')

    const dl = page.locator('dl')
    await expect(dl).toContainText('Viewer')
    await expect(page.locator('table')).not.toContainText('ApproveExceptions')
  })
})

test.describe('Exception Approval Worklist enforces ApproveExceptions', () => {
  test('Approver can load the worklist', async ({ page }) => {
    await signInAs(page, 'TestUser.Approver')

    await page.goto('/exceptions/approvals')

    await expect(page.getByRole('heading', { name: 'Exception Approval Worklist' })).toBeVisible()
    // Not asserting the worklist is empty: other tests in this suite
    // (account-progress-and-risk-exceptions.spec.js) legitimately create
    // real Active exceptions against the same BlueTrackTest database, so
    // "empty" stopped being a safe invariant once that coverage existed.
    // The real thing this test guards is "loads successfully as Approver,
    // not a permission error" -- confirmed by the heading rendering at all
    // (the Viewer-denied case below is what checks for the error text).
    await expect(page.getByText('Loading...')).toHaveCount(0)
  })

  test('Viewer is sent to Access Denied, not shown the API refusal', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')

    await page.goto('/exceptions/approvals')

    await expect(page.getByRole('heading', { name: 'Access Denied' })).toBeVisible() // D-304: the router, not the API's 403 text
  })
})
