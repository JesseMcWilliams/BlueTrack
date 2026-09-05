import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// Layer 4: the three list/worklist pages with zero prior E2E coverage --
// AccountProgressList.vue, RiskExceptionsList.vue,
// RiskExceptionsOverdueWorklist.vue -- all confirmed genuinely built
// (D-42 stacked filters + multi-column sort, real fetch calls, real
// click-through to detail) by reading each .vue source before writing these.

test.describe('Account Progress List', () => {
  test('Viewer can load the list, filter by stage, sort a column, and click through to a detail', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/accounts')

    await expect(page.getByRole('heading', { name: 'Account Progress' })).toBeVisible()
    // .count() reads the DOM once and doesn't auto-wait like toHaveCount()
    // does -- confirmed directly after this line read 0 despite the table
    // (with all 4 synthetic accounts) rendering correctly moments later, a
    // race against the initial fetch rather than a real empty-list bug.
    // Waiting for "Loading..." to clear first, as the rest of this test
    // already does before its own filtered-row checks, avoids the race.
    await expect(page.getByText('Loading...')).toHaveCount(0)
    await expect(page.getByText(/^Could not load accounts:/)).toHaveCount(0)
    const rowCountBeforeFilter = await page.locator('tbody tr').count()
    expect(rowCountBeforeFilter).toBeGreaterThan(0)

    // Filtering is a stacked, server-side AND filter (D-42) -- assert the
    // property that holds regardless of exact row counts (every visible
    // row matches the filter), not a fixed count, since other tests in
    // this suite mutate synthetic accounts' stage/status.
    await page.getByLabel('Stage:').selectOption('Onboarded to Vault')
    await expect(page.getByText('Loading...')).toHaveCount(0)
    const filteredRows = page.locator('tbody tr')
    const filteredCount = await filteredRows.count()
    expect(filteredCount).toBeGreaterThan(0)
    for (let i = 0; i < filteredCount; i++) {
      await expect(filteredRows.nth(i).locator('td').nth(1)).toHaveText('Onboarded to Vault')
    }
    await page.getByLabel('Stage:').selectOption('')

    // Sort: a plain click makes the column the sole ascending sort key
    // (D-92: the ARIA APG Sortable Table pattern -- a real <button> inside
    // the <th>, aria-sort on the <th> itself).
    await page.getByRole('button', { name: /^Account/ }).click()
    await expect(page.getByRole('columnheader', { name: /Account/ })).toHaveAttribute('aria-sort', 'ascending')

    const targetRow = page.locator('tbody tr', { hasText: 'TestAccount03' })
    await expect(targetRow).toBeVisible()
    // D-92: the account name is now a real <a> (not the whole <tr>), so
    // native keyboard/AT navigation works -- click it specifically.
    await targetRow.getByRole('link').click()
    await expect(page).toHaveURL(/\/accounts\/\d+$/)
    await expect(page.getByRole('heading', { name: /Account Progress — TestAccount03/ })).toBeVisible()
  })
})

test.describe('Risk Exceptions List', () => {
  test('Approver sees the New Exception link', async ({ page }) => {
    await signInAs(page, 'TestUser.Approver')
    await page.goto('/exceptions')
    await expect(page.getByRole('link', { name: '+ New Exception' })).toBeVisible()
  })

  test('Viewer does not see the New Exception link', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/exceptions')
    await expect(page.getByRole('link', { name: '+ New Exception' })).toHaveCount(0)
  })

  test('Viewer can load the list, filter by status, sort a column, and click through to an exception', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/exceptions')

    await expect(page.getByRole('heading', { name: 'Risk Exceptions' })).toBeVisible()
    await expect(page.getByText(/^Could not load exceptions:/)).toHaveCount(0)

    // Other tests in this suite (account-progress-and-risk-exceptions.spec.js)
    // create real Active exceptions against the same BlueTrackTest database,
    // so at least one Active row is expected here -- not asserting an exact
    // count, matching the lesson already applied in permission-boundaries.spec.js.
    await page.getByLabel('Status:').selectOption('Active')
    await expect(page.getByText('Loading...')).toHaveCount(0)
    const activeRows = page.locator('tbody tr')
    const activeCount = await activeRows.count()
    expect(activeCount).toBeGreaterThan(0)
    for (let i = 0; i < activeCount; i++) {
      await expect(activeRows.nth(i).locator('td').last()).not.toHaveText('')
    }

    await page.getByRole('button', { name: /^Exception ID/ }).click()
    await expect(page.getByRole('columnheader', { name: /Exception ID/ })).toHaveAttribute('aria-sort', 'ascending')

    const firstRow = page.locator('tbody tr').first()
    await firstRow.getByRole('link').click()
    await expect(page).toHaveURL(/\/exceptions\/\d+$/)
  })
})

test.describe('Overdue Exception Reviews worklist', () => {
  test('Approver can load the worklist and see an exception past its review date', async ({ page }) => {
    await signInAs(page, 'TestUser.Approver')

    // Create a genuinely overdue exception (past review date) -- neither
    // RiskExceptionEdit.vue's date input nor RiskExceptionsController.Create
    // reject a past date (confirmed by reading both), matching the same
    // pattern the xUnit RiskExceptionRepositoryTests use for this exact scenario.
    await page.goto('/exceptions/new')
    const accountsResponse = await page.request.get('/api/account-progress')
    const accounts = await accountsResponse.json()
    const account = accounts.find(a => a.accountName === 'TestAccount03')
    await page.getByLabel('Account Key:').fill(String(account.accountKey))
    // Unique per run -- RiskExceptionsController has no Delete endpoint
    // (an established pattern across this suite), so a fixed literal
    // string here collided with a leftover row from an earlier run
    // (confirmed directly: a strict-mode violation on 2 matching rows).
    const justification = `E2E overdue-review worklist fixture ${Date.now()}`
    await page.getByLabel('Justification:').fill(justification)
    const pastReviewDate = new Date(Date.now() - 5 * 24 * 60 * 60 * 1000).toISOString().slice(0, 10)
    await page.getByLabel('Review Date:').fill(pastReviewDate)
    await page.getByRole('button', { name: 'Create Exception' }).click()
    await expect(page.getByText('Status')).toBeVisible()

    await page.goto('/exceptions/overdue')
    await expect(page.getByRole('heading', { name: 'Overdue Exception Reviews' })).toBeVisible()
    await expect(page.getByText(/^Could not load exceptions:/)).toHaveCount(0)
    await expect(page.locator('tbody tr', { hasText: justification })).toBeVisible()

    const overdueRow = page.locator('tbody tr', { hasText: justification })
    await overdueRow.getByRole('link').click()
    await expect(page).toHaveURL(/\/exceptions\/\d+$/)
  })
})
