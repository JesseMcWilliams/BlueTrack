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

    // D-114: a successful save now navigates back to the Accounts list
    // instead of staying on this page and falling back to a read-only <dl>
    // -- if the save had failed, the form (with its error message) would
    // still be showing on this same route instead.
    await expect(page).toHaveURL(/\/accounts$/)
    await expect(page.getByRole('heading', { name: 'Account Progress' })).toBeVisible()
  })

  test('Viewer sees the read-only view, never the edit form', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    const accountKey = await getTestAccountKey(page, 'TestAccount03')

    await page.goto(`/accounts/${accountKey}`)

    await expect(page.locator('form')).toHaveCount(0)
    await expect(page.locator('dl')).toBeVisible()
  })
})

// D-101-105 Phase E: the inline "Edit Override" action on the Account
// Progress list -- confirmed by reading AccountProgressList.vue before
// writing these, same as every other describe block in this file.
test.describe('Account Progress list -- risk score override', () => {
  // D-120: the named band (web.dim_risk_score_band) now shows alongside
  // the effective risk score -- confirmed by reading AccountProgressList.vue
  // before writing this, same as every other describe block in this file.
  test('Risk Band column is present', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/accounts')

    await expect(page.locator('th', { hasText: 'Risk Band' })).toBeVisible()
  })

  test('Approver (who holds EditAccountProgress) can set then clear an override', async ({ page }) => {
    // Found 2026-09-23: this test targeted an inline "Edit Override" button
    // on the list row that no longer exists anywhere in the app -- override
    // editing was refactored onto the account Detail page's own "Risk
    // Score" tab as a direct always-editable field (AccountProgressDetail.vue's
    // own comment: "Edit Override" (removed)) -- and the column index this
    // test read from (`td.nth(6)`) was also stale against the list's
    // current column order. Not a timing/environment issue at all; the
    // test never matched the current UI. Rewritten against the real flow.
    await signInAs(page, 'TestUser.Approver')
    const accountKey = await getTestAccountKey(page, 'TestAccount03')
    await page.goto(`/accounts/${accountKey}`)

    await page.getByRole('tab', { name: 'Risk Score' }).click()

    // Setting a score with no Reason is rejected client-side before any request.
    await page.getByLabel('Override Score (0-1000, blank clears it):').fill('750')
    await page.getByRole('button', { name: 'Save Override' }).click()
    await expect(page.getByText('A Reason is required when setting an override.')).toBeVisible()

    await page.getByLabel('Reason:').fill('Playwright E2E override')
    await page.getByRole('button', { name: 'Save Override' }).click()

    await expect(page.locator('dl')).toContainText('750')

    // Clearing the override needs no Reason.
    await page.getByLabel('Override Score (0-1000, blank clears it):').fill('')
    await page.getByRole('button', { name: 'Save Override' }).click()

    await expect(page.locator('dl')).not.toContainText('750')
  })

  test('Viewer (who does not hold EditAccountProgress) sees no Edit Override button', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/accounts')

    const row = page.locator('tbody tr', { hasText: 'TestAccount03' })
    await expect(row.getByRole('button', { name: 'Edit Override' })).toHaveCount(0)
  })
})

test.describe('Account Progress bulk edit (D-182)', () => {
  test('Analyst selects accounts, uses the selection buttons, and bulk edits a field', async ({ page }) => {
    await signInAs(page, 'TestUser.Analyst')
    await page.goto('/accounts')
    await page.getByPlaceholder('username, address or account name...').fill('TestAccount0')
    await expect(page.locator('tbody tr')).toHaveCount(4)

    await page.getByLabel('Select accounts for bulk actions').check()
    const bar = page.locator('.selection-bar')
    await bar.getByRole('button', { name: 'Select all on page' }).click()
    await expect(bar).toContainText('4 selected')
    await bar.getByRole('button', { name: 'Invert selection on page' }).click()
    await expect(bar).toContainText('0 selected')

    const rows = page.locator('tbody tr')
    await rows.filter({ hasText: 'TestAccount01' }).getByRole('checkbox').check()
    await rows.filter({ hasText: 'TestAccount02' }).getByRole('checkbox').check()
    await expect(bar).toContainText('2 selected')

    // Remember the two accounts' current Business Unit, to put back afterwards.
    const keys = []
    for (const name of ['TestAccount01', 'TestAccount02']) {
      const href = await rows.filter({ hasText: name }).getByRole('link').first().getAttribute('href')
      keys.push(Number(href.split('/').pop()))
    }
    const originals = []
    for (const key of keys) {
      const detail = await (await page.request.get(`/api/account-progress/${key}`)).json()
      originals.push({ key, businessUnit: detail.businessUnit })
    }

    try {
      await bar.getByRole('button', { name: 'Bulk edit…' }).click()
      await expect(page).toHaveURL(/\/accounts\/bulk-edit$/)
      await expect(page.getByText('2 accounts selected')).toBeVisible()
      await page.getByLabel('Change Business Unit').check()
      await page.getByLabel('Business Unit', { exact: true }).fill('E2E Bulk Unit')
      await page.getByRole('button', { name: 'Apply to 2 accounts' }).click()
      await expect(page.getByText('2 updated, 0 already had these values, 0 skipped.')).toBeVisible()

      await page.getByRole('button', { name: 'Back to Accounts' }).click()
      await expect(page).toHaveURL(/\/accounts$/)
    } finally {
      for (const o of originals) {
        await page.request.post('/api/account-progress/bulk-edit', {
          data: { accountKeys: [o.key], fields: ['BusinessUnit'], businessUnit: o.businessUnit }
        })
      }
    }
  })

  test('Viewer does not see the bulk edit selection', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/accounts')
    await expect(page.locator('th', { hasText: 'Risk Band' })).toBeVisible()
    await expect(page.getByLabel('Select accounts for bulk actions')).toHaveCount(0)
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

    await page.getByLabel('Reason to revoke:').fill('E2E: no longer needed')
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

test.describe('Risk Exceptions bulk import (D-183)', () => {
  test('Approver opens Bulk Actions, uploads a file, and sees the row errors', async ({ page }) => {
    await signInAs(page, 'TestUser.Approver')
    await page.goto('/exceptions')
    await page.getByRole('button', { name: 'Bulk Actions' }).click()
    await expect(page).toHaveURL(/\/exceptions\/bulk-import$/)
    await expect(page.getByRole('link', { name: 'Download template' })).toBeVisible()

    // A row that can't be imported (no such account), so nothing is created.
    const csv = 'SourceTool,SourceExceptionId,SourceUrl,AccountUserName,AccountAddress,ApplicationCode,Justification,ApprovedByName,ApprovalDate,ReviewDate,Status,ExternalTicketReference,LinkToAccountProgress\r\n' +
      'E2E-GRC,E2E-1,,NoSuchE2EUser,nowhere.example.com,,Test,Pat Approver,2026-01-15,2027-01-15,,,\r\n'
    await page.getByLabel('Risk exceptions CSV file').setInputFiles({ name: 'exceptions.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) })
    await page.getByRole('button', { name: 'Import' }).click()

    const result = page.getByTestId('exceptions-result')
    await expect(result).toContainText('1 rows -- 0 imported, 0 linked to Account Progress, 1 errors')
    await expect(result).toContainText("Row 2: No account with username 'NoSuchE2EUser' and address 'nowhere.example.com'.")
  })

  test('Analyst (no ApproveExceptions) has no Bulk Actions button', async ({ page }) => {
    await signInAs(page, 'TestUser.Analyst')
    await page.goto('/exceptions')
    await expect(page.locator('h1', { hasText: 'Risk Exceptions' })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Bulk Actions' })).toHaveCount(0)
  })
})

test.describe('Account delete and undelete (D-185)', () => {
  test('Admin deletes an account with a reason, finds it under Only deleted, and undeletes it in bulk', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/accounts')
    await page.getByPlaceholder('username, address or account name...').fill('TestAccount01')
    const row = page.locator('tbody tr', { hasText: 'TestAccount01' })
    const href = await row.getByRole('link').first().getAttribute('href')
    const accountKey = Number(href.split('/').pop())

    try {
      await row.getByRole('link').first().click()
      await page.getByRole('button', { name: 'Delete account…' }).click()
      await page.getByLabel('Reason to delete:').fill('E2E: duplicate record')
      await page.getByRole('button', { name: 'Delete', exact: true }).click()
      await expect(page.getByRole('note')).toContainText('This account is deleted.')
      await expect(page.getByRole('note')).toContainText('E2E: duplicate record')
      await expect(page.getByText(/Delete \/ undelete history \(\d+\)/)).toBeVisible()

      await page.goto('/accounts')
      await page.getByPlaceholder('username, address or account name...').fill('TestAccount01')
      await expect(page.locator('tbody tr', { hasText: 'TestAccount01' })).toHaveCount(0)
      await page.getByLabel('Deleted accounts:').selectOption('Only')
      const deletedRow = page.locator('tbody tr', { hasText: 'TestAccount01' })
      await expect(deletedRow.locator('.deleted-badge')).toBeVisible()

      await page.getByLabel('Select accounts for bulk actions').check()
      await deletedRow.getByRole('checkbox').check()
      await page.getByRole('button', { name: 'Undelete…' }).click()
      await page.getByLabel(/Reason to undelete 1 account/).fill('E2E: deleted by mistake')
      await page.getByRole('button', { name: 'Undelete', exact: true }).click()
      await expect(page.getByText('1 undeleted, 0 skipped.')).toBeVisible()
    } finally {
      await page.request.post('/api/account-progress/undelete', { data: { accountKeys: [accountKey], reason: 'E2E cleanup' } })
      // Opening the account took the edit lock; left held, a re-run within the lock timeout finds TestAccount01 "being edited".
      await page.request.delete(`/api/account-progress/${accountKey}/lock`)
    }
  })

  test('Analyst (no DeleteAccounts) has no delete buttons', async ({ page }) => {
    await signInAs(page, 'TestUser.Analyst')
    await page.goto('/accounts')
    await page.getByLabel('Select accounts for bulk actions').check()
    await expect(page.getByRole('button', { name: 'Bulk edit…' })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Delete…' })).toHaveCount(0)
  })
})

test.describe('Bulk actions on Risk Exceptions (D-190)', () => {
  test('Approver bulk revokes selected exceptions with a reason', async ({ page }) => {
    await signInAs(page, 'TestUser.Approver')
    const accounts = await (await page.request.get('/api/account-progress?search=TestAccount03')).json()
    const accountKey = accounts.find(a => a.accountName === 'TestAccount03').accountKey
    const reviewDate = new Date(Date.now() + 30 * 24 * 60 * 60 * 1000).toISOString().slice(0, 10)
    const ids = []
    for (const n of [1, 2]) {
      const created = await (await page.request.post('/api/risk-exceptions', {
        data: { accountKey, justification: `E2E bulk revoke ${Date.now()}-${n}`, reviewDate }
      })).json()
      ids.push((await (await page.request.get(`/api/risk-exceptions/${created.exceptionKey}`)).json()).exceptionID)
    }

    await page.goto('/exceptions')
    await page.getByLabel('Status:').selectOption('Active')
    // Newest first, so the two just created are on page 1 however many Active exceptions earlier runs left behind.
    await page.getByRole('button', { name: /^Exception ID/ }).click()
    await page.getByRole('button', { name: /^Exception ID/ }).click()
    await expect(page.getByRole('columnheader', { name: /Exception ID/ })).toHaveAttribute('aria-sort', 'descending')
    await page.getByLabel('Select exceptions for bulk actions').check()
    for (const id of ids) await page.getByLabel(`Select ${id}`, { exact: true }).check()
    await page.getByRole('button', { name: 'Revoke…' }).click()
    await page.getByLabel(/Reason to revoke 2 exception/).fill('E2E: remediated')
    await page.getByRole('button', { name: 'Revoke', exact: true }).click()
    await expect(page.getByText('2 revoked, 0 skipped.')).toBeVisible()
  })
})
