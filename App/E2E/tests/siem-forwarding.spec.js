import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// D-234: Admin > SIEM Forwarding. Opens the page, changes settings and an
// event type's severity, saves (forwarding stays off, so nothing is sent
// anywhere), checks they stick, then puts the original settings back.

test.describe('SIEM Forwarding', () => {
  test('Admin saves the settings and an event type severity', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    const original = await (await page.request.get('/api/admin/siem-forwarding')).json()
    try {
      await page.goto('/admin')
      await page.getByRole('link', { name: 'SIEM Forwarding' }).click()
      await expect(page.getByRole('heading', { name: 'SIEM Forwarding' })).toBeVisible()
      await expect(page.getByLabel('Forward the audit log to the SIEM')).not.toBeChecked()

      await page.getByLabel('Host', { exact: true }).fill('siem.e2e.invalid')
      await page.getByRole('combobox', { name: 'Transport' }).selectOption({ label: 'TCP, not encrypted (RFC 6587)' })
      await expect(page.getByLabel('Port', { exact: true })).toHaveValue('514')
      await page.getByLabel('App name').fill('BlueTrackE2E')
      await page.getByLabel('Severity of ReportExport').fill('6')
      await page.getByRole('button', { name: 'Save' }).click()
      await expect(page.getByText('Settings saved.')).toBeVisible()

      await page.reload()
      await expect(page.getByLabel('Host', { exact: true })).toHaveValue('siem.e2e.invalid')
      await expect(page.getByRole('combobox', { name: 'Transport' })).toHaveValue('Tcp')
      await expect(page.getByLabel('App name')).toHaveValue('BlueTrackE2E')
      await expect(page.getByLabel('Severity of ReportExport')).toHaveValue('6')
    } finally {
      const restored = await page.request.put('/api/admin/siem-forwarding', {
        data: {
          ...original.settings,
          eventTypes: original.eventTypes.map(t => ({ auditEventTypeKey: t.auditEventTypeKey, isForwarded: t.isForwarded, cefSeverity: t.cefSeverity }))
        }
      })
      expect(restored.ok()).toBe(true)
    }
  })

  test('Viewer has no SIEM Forwarding section and the API refuses it', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/admin')
    await expect(page.getByRole('link', { name: 'SIEM Forwarding' })).toHaveCount(0)
    expect((await page.request.get('/api/admin/siem-forwarding')).status()).toBe(403)
  })
})
