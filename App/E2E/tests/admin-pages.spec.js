import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'

// Layer 4: the 8 Admin sub-pages (D-47) -- confirmed genuinely built (not
// placeholders) by reading each .vue source before writing these, not
// assumed from the router's route list alone. AdminHub.vue gates which
// nav links render per rights.hasPermission, but does NOT block direct
// navigation to a sub-page's own route -- that page's own fetch calls hit
// the real 403 from the API and show it as plain error text (no
// permission-specific message, unlike RiskExceptionEdit's create form).

test.describe('Admin Hub navigation is gated per permission', () => {
  test('Admin sees all 8 admin sections', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin')

    const nav = page.locator('nav.admin-subnav')
    await expect(nav.getByRole('link', { name: 'Identity Providers' })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Group → Role Mapping' })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Roles & Permissions' })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Application ↔ Safe Mapping' })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Secrets Store Configuration' })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Field Metadata Management' })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Audit Log Viewer' })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Global Application Configuration' })).toBeVisible()
  })

  test('Viewer sees only Audit Log Viewer, the one admin permission Viewer holds', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/admin')

    const nav = page.locator('nav.admin-subnav')
    await expect(nav.getByRole('link', { name: 'Audit Log Viewer' })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Identity Providers' })).toHaveCount(0)
    await expect(nav.getByRole('link', { name: 'Roles & Permissions' })).toHaveCount(0)
  })
})

test.describe('Identity Providers admin page', () => {
  test('Admin can create, edit, and delete a provider', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/identity-providers')
    const displayName = `E2E Test Provider ${Date.now()}`

    await page.getByRole('button', { name: '+ New Provider' }).click()
    await page.getByLabel('Display Name:').fill(displayName)
    await page.locator('form button[type="submit"]').click()

    const row = page.locator('tbody tr', { hasText: displayName })
    await expect(row).toBeVisible()

    await row.getByRole('button', { name: 'Edit' }).click()
    const updatedName = `${displayName} (Updated)`
    await page.getByLabel('Display Name:').fill(updatedName)
    await page.locator('form button[type="submit"]').click()
    await expect(page.locator('tbody tr', { hasText: updatedName })).toBeVisible()

    await page.locator('tbody tr', { hasText: updatedName }).getByRole('button', { name: 'Delete' }).click()
    await expect(page.locator('tbody tr', { hasText: updatedName })).toHaveCount(0)
  })

  // D-95: OIDC/SAML's ConfigurationValues moved from a raw JSON textarea to
  // structured per-type fields -- this confirms the round trip actually
  // works end to end (save structured fields -> reload -> re-open edit ->
  // the same values come back populated into the same structured fields),
  // not just that the form still submits.
  test('OIDC structured config fields round-trip through save and reload', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/identity-providers')
    const displayName = `E2E OIDC Provider ${Date.now()}`

    await page.getByRole('button', { name: '+ New Provider' }).click()
    await page.getByLabel('Display Name:').fill(displayName)
    await page.getByLabel('Authority:').fill('https://login.example.com/tenant123/v2.0')
    await page.getByLabel('Client ID:').fill('e2e-client-id')
    await page.getByLabel('Groups Claim Type:').fill('e2e-groups-claim')
    await page.locator('form button[type="submit"]').click()

    const row = page.locator('tbody tr', { hasText: displayName })
    await expect(row).toBeVisible()

    try {
      await row.getByRole('button', { name: 'Edit' }).click()
      await expect(page.getByLabel('Authority:')).toHaveValue('https://login.example.com/tenant123/v2.0')
      await expect(page.getByLabel('Client ID:')).toHaveValue('e2e-client-id')
      await expect(page.getByLabel('Callback Path:')).toHaveValue('/signin-oidc')
      await expect(page.getByLabel('Groups Claim Type:')).toHaveValue('e2e-groups-claim')
      await page.getByRole('button', { name: 'Cancel' }).click()
    } finally {
      await row.getByRole('button', { name: 'Delete' }).click()
      await expect(row).toHaveCount(0)
    }
  })
})

test.describe('Group → Role Mapping admin page', () => {
  test('Admin can add and delete a mapping, and use the lookup tool', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/group-role-mapping')

    // The table's "Group (stored identifier)" column shows the RESOLVED
    // SID, not the friendly name typed into the form (D-69) -- BUILTIN\Users
    // resolves to the well-known, machine-independent SID S-1-5-32-545
    // (confirmed directly in this table and in the audit log), so that's
    // what every lookup below has to match on, not the literal "BUILTIN\Users" text.
    const builtinUsersSid = 'S-1-5-32-545'

    // Self-healing: identity_group_role_map has a UNIQUE (ProviderKey,
    // IdentityGroupName, AppRoleKey) constraint that a duplicate insert
    // violates as an unhandled 500, not a clean 409 -- confirmed directly
    // after a prior interrupted run left this exact mapping behind. Clear
    // it first so this test is safe to re-run even after an earlier
    // failure skipped its own cleanup.
    const leftoverRow = page.locator('tbody tr', { hasText: builtinUsersSid })
    if (await leftoverRow.count() > 0) {
      await leftoverRow.getByRole('button', { name: 'Delete' }).click()
      await expect(leftoverRow).toHaveCount(0)
    }

    await page.getByLabel(/^Group Name/).fill('BUILTIN\\Users')
    // D-93-adjacent fix: Role is now a real <select> populated from
    // GET /api/admin/group-role-mappings/roles, not free text -- confirms
    // the admin picks an actual existing role rather than typing one.
    await page.getByLabel('Role:').selectOption('Viewer')
    await page.getByRole('button', { name: 'Add' }).click()

    const row = page.locator('tbody tr', { hasText: builtinUsersSid })
    await expect(row).toBeVisible()
    await row.getByRole('button', { name: 'Delete' }).click()
    await expect(row).toHaveCount(0)

    await page.getByPlaceholder('Group name').fill('BUILTIN\\Users')
    await page.getByRole('button', { name: 'Resolve' }).click()
    await expect(page.getByText(/Resolved to:.*S-1-/)).toBeVisible()
  })
})

test.describe('Roles & Permissions admin page', () => {
  test('Admin can create a role with a permission, then delete it', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/roles-permissions')
    const roleName = `E2ETestRole${Date.now()}`

    await page.getByRole('button', { name: '+ New Role' }).click()
    await page.getByLabel('Role Name:').fill(roleName)
    // A plain string (substring match) is used here, not an anchored regex --
    // confirmed directly that regex hasText tests the untrimmed text node
    // ("<input/> ViewDashboard — ..." has a literal leading space from the
    // template), so a `^`-anchored pattern never matches while the
    // whitespace-insensitive string form correctly resolves to exactly one label.
    await page.locator('label', { hasText: 'ViewDashboard' }).locator('input[type="checkbox"]').check()
    await page.locator('form button[type="submit"]').click()

    const row = page.locator('tbody tr', { hasText: roleName })
    await expect(row).toBeVisible()
    await expect(row).toContainText('ViewDashboard')

    await row.getByRole('button', { name: 'Delete' }).click()
    await expect(row).toHaveCount(0)
  })
})

test.describe('Application ↔ Safe Mapping admin page', () => {
  test('Admin can create an application and assign it to the synthetic test safe', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/application-mapping')
    const suffix = `${Date.now()}`
    const appName = `E2E Test Application ${suffix}`

    await page.getByRole('button', { name: '+ New Application' }).click()
    const appCode = `E2EAPP${suffix}`
    await page.getByLabel('Code:').fill(appCode)
    // exact: true -- "Name:" would otherwise substring-match "Owner Name:"/"Technical Contact Name:" too.
    await page.getByLabel('Name:', { exact: true }).fill(appName)
    await page.locator('form button[type="submit"]').click()
    // Scoped to the Code cell specifically -- a plain hasText match on the
    // whole row would also match every Safes-table row, since each one's
    // <select> renders an <option> per application (this app's name
    // included) into its own textContent regardless of which is selected.
    const appRow = page.getByRole('cell', { name: appCode, exact: true }).locator('..')
    await expect(appRow).toBeVisible()

    const safeRow = page.getByRole('cell', { name: 'TestSafe01', exact: true }).locator('..')
    try {
      await safeRow.locator('select').selectOption({ label: appName })
      await expect(safeRow.locator('select option:checked')).toHaveText(appName)
    } finally {
      // Leave the assignment cleared so this shared synthetic fixture stays
      // the way other tests expect it (matches AdminControllersFunctionalTests'
      // own cleanup convention for TestSafe01).
      await safeRow.locator('select').selectOption({ label: '(none)' })
      await expect(safeRow.locator('select option:checked')).toHaveText('(none)')
    }
  })
})

test.describe('Secrets Store Configuration admin page', () => {
  test('Admin can activate a different backend, then restore Windows DPAPI', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/secrets-store')

    const dpapiRow = page.locator('tbody tr', { hasText: 'WindowsDpapi' })
    await expect(dpapiRow).toContainText('Yes')

    try {
      const cyberArkRow = page.locator('tbody tr', { hasText: 'CyberArkCP' })
      await cyberArkRow.getByRole('button', { name: 'Make Active' }).click()
      await expect(cyberArkRow).toContainText('Yes')
      await expect(dpapiRow).not.toContainText('Yes')
    } finally {
      await dpapiRow.getByRole('button', { name: 'Make Active' }).click()
      await expect(dpapiRow).toContainText('Yes')
    }
  })

  // D-95: CyberArkCP's Settings moved from a raw JSON textarea to a
  // structured App ID field -- confirms it actually round-trips (save ->
  // reload -> the same value comes back populated), not just that the
  // form still submits.
  test('CyberArkCP structured App ID field round-trips through save and reload', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/secrets-store')

    const dpapiRow = page.locator('tbody tr', { hasText: 'WindowsDpapi' })
    const cyberArkRow = page.locator('tbody tr', { hasText: 'CyberArkCP' })

    try {
      await cyberArkRow.getByLabel('App ID:').fill('e2e-app-id')
      await cyberArkRow.getByRole('button', { name: 'Make Active' }).click()
      await expect(cyberArkRow).toContainText('Yes')

      await page.reload()
      await expect(page.locator('tbody tr', { hasText: 'CyberArkCP' }).getByLabel('App ID:')).toHaveValue('e2e-app-id')
    } finally {
      await dpapiRow.getByRole('button', { name: 'Make Active' }).click()
      await expect(dpapiRow).toContainText('Yes')
    }
  })
})

test.describe('Field Metadata Management admin page', () => {
  test('Admin can create, edit, and delete a field definition', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/field-metadata')
    const fieldName = `E2ETestField${Date.now()}`

    await page.getByRole('button', { name: '+ New Field' }).click()
    await page.getByLabel('Field Name:').fill(fieldName)
    await page.getByLabel('Display Label:').fill('E2E Test Field Label')
    await page.locator('form button[type="submit"]').click()

    const row = page.locator('tbody tr', { hasText: fieldName })
    await expect(row).toBeVisible()

    await row.getByRole('button', { name: 'Edit' }).click()
    await page.getByLabel('Display Label:').fill('Updated Label')
    await page.locator('form button[type="submit"]').click()
    await expect(page.locator('tbody tr', { hasText: fieldName })).toContainText('Updated Label')

    await page.locator('tbody tr', { hasText: fieldName }).getByRole('button', { name: 'Delete' }).click()
    await expect(page.locator('tbody tr', { hasText: fieldName })).toHaveCount(0)
  })
})

test.describe('Audit Log Viewer admin page', () => {
  test('Viewer (who holds ViewAuditLog) can load and filter the log', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/admin/audit-log')

    await expect(page.getByText('Loading...')).toHaveCount(0)

    await page.getByPlaceholder('e.g. FieldEdit').fill('FieldEdit')
    await page.getByRole('button', { name: 'Filter' }).click()
    await expect(page.getByText('Loading...')).toHaveCount(0)
  })

  test('A user with no role mapping (no permissions at all) is denied with a plain error', async ({ page }) => {
    // Viewer, Analyst, and Approver all hold ViewAuditLog per
    // Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql -- only a
    // username with no identity_group_role_map row at all resolves to zero
    // permissions, matching AdminControllersPermissionTests.cs's own use of
    // TestUser.DoesNotExist for this exact case. DevTestAuthController only
    // requires the TestUser.<Role> shape to sign in, not a seeded mapping.
    await signInAs(page, 'TestUser.DoesNotExist')
    await page.goto('/admin/audit-log')

    await expect(page.getByText(/Request failed: 403/)).toBeVisible()
  })
})

test.describe('Global Application Configuration admin page', () => {
  test('Admin can update a setting, save, then restore the original value', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/configuration')

    const idleTimeoutInput = page.getByLabel('Idle Timeout (minutes):')
    const originalValue = await idleTimeoutInput.inputValue()

    await idleTimeoutInput.fill(String(Number(originalValue) + 1))
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.getByText('Saved.')).toBeVisible()

    await idleTimeoutInput.fill(originalValue)
    await page.getByRole('button', { name: 'Save' }).click()
    await expect(page.getByText('Saved.')).toBeVisible()
  })
})
