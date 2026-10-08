import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// Layer 4: the theme picker (Design_Accessibility-And-Theming.md, D-93) --
// confirms the real end-to-end path: picking a theme in MyProfile.vue
// applies <html data-theme>, persists server-side (web.user_preference via
// PUT /api/me/preferences/Theme), and survives a fresh page load (proving
// the server round trip actually wrote the preference, not just the
// client-side localStorage mirror).

// Picking a theme applies it at once (and mirrors it to localStorage), but
// saves it to the server asynchronously. Wait for that PUT before anything
// that reads the saved value (a reload) or the next test: without it, a fast
// reload intermittently showed the previous theme's radio button checked
// (found 2026-10-08 in back-to-back full runs).
async function pickTheme(page, name) {
  const radio = page.getByRole('radio', { name })
  // Already selected (e.g. left over from an interrupted run): checking it
  // again changes nothing and sends no PUT, so there's nothing to wait for.
  if (await radio.isChecked()) return
  const saved = page.waitForResponse(r => r.url().includes('/api/me/preferences/Theme') && r.request().method() === 'PUT')
  await radio.check()
  await saved
}

test.describe('Theme picker', () => {
  test('Selecting a theme applies it immediately and persists across a reload', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/profile')

    await pickTheme(page, 'Dark')
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'Dark')

    await page.reload()
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'Dark')
    await expect(page.getByRole('radio', { name: 'Dark' })).toBeChecked()

    // Leave this synthetic user's preference back at a clean default so
    // other test runs against the same shared BlueTrackTest database don't
    // inherit a leftover Dark preference.
    await pickTheme(page, 'Light')
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'Light')
  })

  test('High Visibility theme is selectable and applies the expected palette', async ({ page }) => {
    await signInAs(page, 'TestUser.Approver')
    await page.goto('/profile')

    await pickTheme(page, 'High Visibility')
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'HighVisibility')
    await expect(page.locator('body')).toHaveCSS('background-color', 'rgb(0, 0, 0)')
    await expect(page.locator('body')).toHaveCSS('color', 'rgb(255, 255, 0)')

    await pickTheme(page, 'Light')
  })
})
