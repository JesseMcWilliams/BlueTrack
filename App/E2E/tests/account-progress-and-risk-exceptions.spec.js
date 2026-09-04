import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// Layer 4: the real Account Progress edit form (locking, D-51-adjacent
// save flow) and the real Risk Exception create/extend/revoke workflow,
// both genuinely built (not placeholders) -- confirmed by reading the
// actual .vue source before writing these, not assumed from the route
// list alone.

async function getTestAccountKey(page, sourceAccountId) {
  // The account list is public data any authenticated user can read
  // (GET /api/account-progress) -- reuse it to find the synthetic
  // account's real AccountKey rather than hardcoding an IDENTITY value.
  // Must be page.request (shares this browser context's cookie jar), not
  // the standalone `request` fixture, which is a separate, unauthenticated
  // context -- confirmed directly (2026-09-04) after this returned an
  // empty 401 body instead of the account list.
  const response = await page.request.get('/api/account-progress')
  const accounts = await response.json()
  const match = accounts.find(a => a.accountName === sourceAccountId)
  if (!match) throw new Error(`Synthetic account ${sourceAccountId} not found -- has Database/Test/02_BlueTrack_Test_SyntheticAccountData.sql been applied to BlueTrackTest?`)
  return match.accountKey
}

test.describe('Account Progress edit form', () => {
  test('Approver can load the edit lock, change a field, and save', async ({ page }) => {
    await signInAs(page, 'TestUser.Approver')
    const accountKey = await getTestAccountKey(page, 'TestAccount03')

    await page.goto(`/accounts/${accountKey}`)

    // Having EditAccountProgress means the page auto-acquires the lock and
    // renders the real editable form, not the read-only <dl>.
    await expect(page.locator('form button[type="submit"]')).toBeVisible()

    await page.fill('form input[type="text"]', 'Playwright E2E Owner')
    await page.click('form button[type="submit"]')

    // A successful save releases the lock and falls back to the read-only
    // <dl> view -- if the save had failed, the form (with its error
    // message) would still be showing instead.
    await expect(page.locator('form')).toHaveCount(0)
    await expect(page.locator('dl')).toBeVisible()
  })

  test('Viewer sees the read-only view, never the edit form', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    const accountKey = await getTestAccountKey(page, 'TestAccount03')

    await page.goto(`/accounts/${accountKey}`)

    await expect(page.locator('form')).toHaveCount(0)
    await expect(page.locator('dl')).toBeVisible()
  })
})

test.describe('Risk Exception create/extend/revoke workflow', () => {
  test('Approver can create, extend, and revoke an exception end to end', async ({ page }) => {
    await signInAs(page, 'TestUser.Approver')
    const accountKey = await getTestAccountKey(page, 'TestAccount03')

    await page.goto('/exceptions/new')
    await page.fill('input[type="number"]', String(accountKey))
    await page.fill('textarea', 'Playwright E2E justification')
    const reviewDate = new Date(Date.now() + 30 * 24 * 60 * 60 * 1000).toISOString().slice(0, 10)
    await page.fill('input[type="date"]', reviewDate)
    await page.click('button[type="submit"]')

    // Create redirects into edit mode for the new exception.
    await expect(page.getByText('Status')).toBeVisible()
    await expect(page.getByText('Active')).toBeVisible()

    const newReviewDate = new Date(Date.now() + 90 * 24 * 60 * 60 * 1000).toISOString().slice(0, 10)
    await page.fill('input[type="date"]', newReviewDate)
    await page.click('button:has-text("Extend Review Date")')
    await expect(page.getByText(newReviewDate)).toBeVisible()

    await page.click('button:has-text("Revoke Exception")')
    await expect(page.getByText('Revoked')).toBeVisible()
  })

  test('Viewer is denied creating an exception with the permission-specific message', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')

    await page.goto('/exceptions/new')
    await page.fill('input[type="number"]', '1')
    await page.fill('textarea', 'Should be rejected')
    const reviewDate = new Date(Date.now() + 30 * 24 * 60 * 60 * 1000).toISOString().slice(0, 10)
    await page.fill('input[type="date"]', reviewDate)
    await page.click('button[type="submit"]')

    await expect(page.getByText('You do not have the ApproveExceptions permission.')).toBeVisible()
  })
})
