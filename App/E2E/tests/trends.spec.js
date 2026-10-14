import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// D-221: Reports > Trends, opened from the Reports column, against the
// snapshots Database/Test/03_BlueTrack_Test_TrendsHistorySeed.sql writes for
// TestAccount01-04 (the 14 days before the database was built; the default
// range is the last 90 days). TestSafe01 holds TestAccount01, 03 and 04.

test('Viewer opens Trends from the Reports column, filters by week and Safe, and reads the data table', async ({ page }) => {
  await signInAs(page, 'TestUser.Viewer')
  await page.goto('/reports')

  await page.locator('nav.reports-subnav').getByRole('link', { name: 'Trends' }).click()
  await expect(page).toHaveURL(/\/reports\/trends$/)
  await expect(page.getByRole('heading', { name: 'Trends', level: 2 })).toBeVisible()

  for (const title of ['Accounts by stage', '% complete', 'Accounts by risk level']) {
    await expect(page.getByRole('heading', { name: title, level: 3 })).toBeVisible()
  }
  await expect(page.locator('.trends-canvas canvas')).toHaveCount(3)

  // By week: the request carries it, and the periods become weeks.
  const weekly = page.waitForResponse(r => r.url().includes('/api/reports/trends?') && r.url().includes('granularity=week'))
  await page.getByLabel('By:').selectOption('week')
  expect((await weekly).status()).toBe(200)

  // One Safe: TestSafe01's three accounts.
  const safeSelect = page.getByLabel('Safe:')
  const safeValue = await safeSelect.locator('option', { hasText: /^TestSafe01 \(/ }).getAttribute('value')
  const bySafe = page.waitForResponse(r => r.url().includes('/api/reports/trends?') && r.url().includes(`safeKey=${safeValue}`))
  await safeSelect.selectOption(safeValue)
  expect((await bySafe).status()).toBe(200)

  const stagesTable = page.locator('#trends-stages-table')
  await expect(stagesTable).toHaveClass(/visually-hidden/)
  await page.locator('button[aria-controls="trends-stages-table"]').click()
  await expect(stagesTable).toBeVisible()
  await expect(stagesTable.locator('thead')).toContainText('Week')
  const lastRow = stagesTable.locator('tbody tr').last()
  await expect(lastRow.locator('th')).toContainText('Week of')
  await expect(lastRow.locator('td').last()).toHaveText('3')
})
