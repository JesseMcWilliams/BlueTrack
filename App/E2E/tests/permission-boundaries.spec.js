import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// Layer 4 (Design_Testing_Strategy.md): confirms the DevFakeAuth test
// matrix (Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql) is
// reachable end to end -- real browser, real running API + built SPA,
// real BlueTrackTest database.
//
// The frontend doesn't yet pre-emptively hide controls based on
// rights.permissionNames (RiskExceptionsApprovalWorklist.vue's own
// comment: "this app doesn't check rights.permissionNames from /api/me
// before rendering yet") -- so this asserts what actually happens today:
// an ApproveExceptions-gated page surfaces the API's 403 as an in-page
// message. When that frontend gating is built, this test's Viewer
// assertion is the one to update to "the approve controls aren't
// rendered at all," per D-78.

test.describe('MyProfile reflects the signed-in DevFakeAuth role', () => {
  test('Approver sees the Approver role and ApproveExceptions permission', async ({ page }) => {
    await signInAs(page, 'TestUser.Approver')

    await page.goto('/profile')

    await expect(page.getByText('Role(s)')).toBeVisible()
    const dl = page.locator('dl')
    await expect(dl).toContainText('Approver')
    await expect(dl).toContainText('ApproveExceptions')
  })

  test('Viewer sees the Viewer role without ApproveExceptions', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')

    await page.goto('/profile')

    const dl = page.locator('dl')
    await expect(dl).toContainText('Viewer')
    await expect(dl).not.toContainText('ApproveExceptions')
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

  test('Viewer is denied with the permission-specific message, not a generic failure', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')

    await page.goto('/exceptions/approvals')

    await expect(page.getByText('You do not have the ApproveExceptions permission.')).toBeVisible()
  })
})
