import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// Layer 4: the language picker (D-227, Claude_Docs/Design_Localization.md).
// Only English has messages today, so the picker lists English alone; the
// page's <html lang> follows the active language, and the choice is stored
// server-side (web.user_preference, key Language) like Theme.
test.describe('Language picker', () => {
  test('lists English (and Use browser default), sets <html lang>, and keeps a saved Language preference', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')

    // Saved through the same generalized preference endpoint the picker uses.
    const saved = await page.request.put('/api/me/preferences/Language', { data: { value: 'en' } })
    expect(saved.status()).toBe(204)

    await page.goto('/profile')
    await expect(page.locator('html')).toHaveAttribute('lang', 'en')

    const group = page.getByRole('group', { name: 'Choose a display language' })
    await expect(group.getByRole('radio')).toHaveCount(2)   // Use browser default, and English
    await expect(group.getByRole('radio', { name: 'English', exact: true })).toBeChecked()

    await page.reload()
    await expect(page.locator('html')).toHaveAttribute('lang', 'en')
    await expect(page.getByRole('heading', { name: 'Profile & Preferences' })).toBeVisible()
  })

  test('Use browser default clears the saved choice, and picking English saves it again', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.request.put('/api/me/preferences/Language', { data: { value: 'en' } })
    await page.goto('/profile')
    const group = page.getByRole('group', { name: 'Choose a display language' })
    await expect(group.getByRole('radio', { name: 'English', exact: true })).toBeChecked()

    await group.getByRole('radio', { name: /Use browser default/ }).check()
    await expect(group.getByRole('radio', { name: /Use browser default/ })).toBeChecked()
    await expect.poll(async () => (await (await page.request.get('/api/me')).json()).preferences.Language).toBeUndefined()

    await page.reload()
    await expect(group.getByRole('radio', { name: /Use browser default/ })).toBeChecked()
    await expect(page.locator('html')).toHaveAttribute('lang', 'en')

    await group.getByRole('radio', { name: 'English', exact: true }).check()
    await expect.poll(async () => (await (await page.request.get('/api/me')).json()).preferences.Language).toBe('en')
    await page.request.delete('/api/me/preferences/Language')
  })
})
