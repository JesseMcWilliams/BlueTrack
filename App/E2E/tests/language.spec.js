import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// Layer 4: the language picker (D-227, Claude_Docs/Design_Localization.md).
// Only English has messages today, so the picker lists English alone; the
// page's <html lang> follows the active language, and the choice is stored
// server-side (web.user_preference, key Language) like Theme.
test.describe('Language picker', () => {
  test('lists English only, sets <html lang>, and keeps a saved Language preference', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')

    // Saved through the same generalized preference endpoint the picker uses.
    const saved = await page.request.put('/api/me/preferences/Language', { data: { value: 'en' } })
    expect(saved.status()).toBe(204)

    await page.goto('/profile')
    await expect(page.locator('html')).toHaveAttribute('lang', 'en')

    const group = page.getByRole('group', { name: 'Choose a display language' })
    await expect(group.getByRole('radio')).toHaveCount(1)
    await expect(group.getByRole('radio', { name: 'English' })).toBeChecked()

    await page.reload()
    await expect(page.locator('html')).toHaveAttribute('lang', 'en')
    await expect(page.getByRole('heading', { name: 'Profile & Preferences' })).toBeVisible()
  })
})
