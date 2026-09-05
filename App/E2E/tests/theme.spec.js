import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// Layer 4: the theme picker (Design_Accessibility_And_Theming.md, D-93) --
// confirms the real end-to-end path: picking a theme in MyProfile.vue
// applies <html data-theme>, persists server-side (web.user_preference via
// PUT /api/me/preferences/Theme), and survives a fresh page load (proving
// the server round trip actually wrote the preference, not just the
// client-side localStorage mirror).

test.describe('Theme picker', () => {
  test('Selecting a theme applies it immediately and persists across a reload', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/profile')

    await page.getByRole('radio', { name: 'Dark' }).check()
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'Dark')

    await page.reload()
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'Dark')
    await expect(page.getByRole('radio', { name: 'Dark' })).toBeChecked()

    // Leave this synthetic user's preference back at a clean default so
    // other test runs against the same shared BlueTrackTest database don't
    // inherit a leftover Dark preference.
    await page.getByRole('radio', { name: 'Light' }).check()
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'Light')
  })

  test('High Visibility theme is selectable and applies the expected palette', async ({ page }) => {
    await signInAs(page, 'TestUser.Approver')
    await page.goto('/profile')

    await page.getByRole('radio', { name: 'High Visibility' }).check()
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'HighVisibility')
    await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(0, 0, 0)')
    await expect(page.locator('body')).toHaveCSS('color', 'rgb(255, 255, 0)')

    await page.getByRole('radio', { name: 'Light' }).check()
  })
})
