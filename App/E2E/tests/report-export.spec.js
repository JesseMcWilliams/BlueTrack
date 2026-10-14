import { readFile } from 'node:fs/promises'
import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'
import { xlsxHeaderRow } from './xlsx.js'

// Layer 4, D-220: a report's Export CSV / Export Excel buttons download the
// whole report as a file named BlueTrack_<Report>_<yyyyMMdd-HHmm>, with the
// grid's columns as the header row. Risk Score (ViewRiskReport, which Admin
// holds) is used because it pages and sorts on the server. Buttons are found
// by role and exact name (getByLabel/getByText match substrings, ignoring
// case -- Reference_Lessons-Learned.md).
const riskScoreHeaders = ['Account', 'Computed', 'Override', 'Effective', 'Risk Band', 'Stale?', 'Last Calculated']

async function exportAs(page, buttonName) {
  const [download] = await Promise.all([
    page.waitForEvent('download'),
    page.getByRole('button', { name: buttonName, exact: true }).click()
  ])
  return { name: download.suggestedFilename(), bytes: await readFile(await download.path()) }
}

test.describe('Report export', () => {
  test('Risk Score downloads as CSV and as Excel, with the grid\'s header row', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/reports/risk-score')
    await expect(page.getByRole('heading', { name: 'Risk Score', exact: true })).toBeVisible()

    // The reports are listed in a column beside the report.
    await expect(page.getByRole('navigation', { name: 'Reports' }).getByRole('link', { name: 'Risk Score', exact: true })).toBeVisible()

    const csv = await exportAs(page, 'Export CSV')
    expect(csv.name).toMatch(/^BlueTrack_RiskScore_\d{8}-\d{4}\.csv$/)
    expect([...csv.bytes.subarray(0, 3)]).toEqual([0xef, 0xbb, 0xbf])
    const firstLine = csv.bytes.subarray(3).toString('utf8').split('\r\n')[0]
    expect(firstLine.split(',')).toEqual(riskScoreHeaders)
    await expect(page.getByRole('status').filter({ hasText: `Downloaded ${csv.name}.` })).toBeVisible()

    const xlsx = await exportAs(page, 'Export Excel')
    expect(xlsx.name).toMatch(/^BlueTrack_RiskScore_\d{8}-\d{4}\.xlsx$/)
    expect(xlsxHeaderRow(xlsx.bytes)).toEqual(riskScoreHeaders)
  })
})
