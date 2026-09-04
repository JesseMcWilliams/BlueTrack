/**
 * Signs in as a simulated DevFakeAuth role via the dev-only test sign-in
 * endpoint (App/Api/Controllers/DevTestAuthController.cs), instead of a
 * real Negotiate handshake -- a real browser here is still authenticated
 * as whichever single Windows account runs it, so DevFakeAuth's normal
 * "authenticated Windows username -> role" lookup can't switch roles per
 * test on its own. This endpoint signs into the same Cookie scheme
 * OIDC/SAML already use, stamping the same bluetrack:provider_type
 * marker claim, so everything downstream (PermissionClaimsTransformation,
 * the real authorization policies) runs unchanged.
 *
 * `page.request` shares this browser context's cookie jar with `page`
 * itself, so a plain API request here is enough -- no need to navigate
 * away and back.
 *
 * @param {import('@playwright/test').Page} page
 * @param {'TestUser.Viewer' | 'TestUser.Analyst' | 'TestUser.Approver' | 'TestUser.Admin'} username
 *   Must match Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql.
 */
export async function signInAs(page, username) {
  const response = await page.request.get(`/api/auth/dev/test-signin?username=${encodeURIComponent(username)}`)
  if (!response.ok()) {
    throw new Error(`Test sign-in as ${username} failed: ${response.status()} ${await response.text()}`)
  }
}
