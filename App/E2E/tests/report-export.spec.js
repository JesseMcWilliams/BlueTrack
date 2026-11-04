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

// D-318: the Accounts list exports, and the export is read back with a changed Owner. TestAccount02, since other
// tests open TestAccount03's edit page, which keeps it locked to them for a while.
test.describe('Accounts export and import', () => {
  test('Admin exports one account, imports it back with a new owner, then puts the owner back', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/accounts?search=TestAccount02')
    await expect(page.locator('tbody tr', { hasText: 'TestAccount02' })).toBeVisible()

    const { name, bytes } = await exportAs(page, 'Export CSV')
    expect(name).toMatch(/^BlueTrack_Accounts_\d{8}-\d{4}\.csv$/)
    const lines = bytes.toString('utf8').replace(/^﻿/, '').split('\r\n').filter(Boolean)
    const headers = lines[0].split(',')
    const ownerAt = headers.indexOf('Owner')
    const keyAt = headers.indexOf('Account Key')
    const row = lines.slice(1).find(l => l.includes('TestAccount02')).split(',')
    const original = row[ownerAt]

    async function importOwner(owner) {
      const csv = `Account Key,Owner\r\n${row[keyAt]},${owner}\r\n`
      if (!(await page.locator('.account-import-form').isVisible())) await page.getByRole('button', { name: 'Import changes...' }).click()
      await page.locator('.account-import-form input[type="file"]').setInputFiles({ name: 'accounts.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) })
      await page.getByRole('button', { name: 'Import', exact: true }).click()
    }

    await importOwner('E2E Imported Owner')
    await expect(page.getByText('Read 1 rows: 1 accounts changed')).toBeVisible()
    await expect(page.locator('tbody tr', { hasText: 'TestAccount02' })).toContainText('E2E Imported Owner')

    await importOwner(original || 'Jane Analyst')
    await expect(page.getByText(/Read 1 rows: 1 accounts changed/)).toBeVisible()
  })
})
