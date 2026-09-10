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
  test('Admin sees all 11 admin sections', async ({ page }) => {
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
    await expect(nav.getByRole('link', { name: 'Deployment' })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Notifications' })).toBeVisible()
    await expect(nav.getByRole('link', { name: 'Credentials & LDAP' })).toBeVisible()
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

// D-124 Phase 4: Add/Edit moved off this page's own inline form onto
// IdentityProviderEdit.vue's routed pages
// (/admin/identity-providers/new, /admin/identity-providers/:providerKey).
test.describe('Identity Providers admin page', () => {
  test('Admin can create, edit, and delete a provider', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/identity-providers')
    const displayName = `E2E Test Provider ${Date.now()}`

    await page.getByRole('button', { name: '+ New Provider' }).click()
    await page.getByLabel('Display Name:').fill(displayName)
    await page.locator('form button[type="submit"]').click()
    await expect(page).toHaveURL(/\/admin\/identity-providers$/)

    const row = page.locator('tbody tr', { hasText: displayName })
    await expect(row).toBeVisible()

    await row.getByRole('link', { name: 'Edit' }).click()
    const updatedName = `${displayName} (Updated)`
    await expect(page.getByLabel('Display Name:')).toHaveValue(displayName)
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
    await expect(page).toHaveURL(/\/admin\/identity-providers$/)

    const row = page.locator('tbody tr', { hasText: displayName })
    await expect(row).toBeVisible()

    try {
      await row.getByRole('link', { name: 'Edit' }).click()
      await expect(page.getByLabel('Authority:')).toHaveValue('https://login.example.com/tenant123/v2.0')
      await expect(page.getByLabel('Client ID:')).toHaveValue('e2e-client-id')
      await expect(page.getByLabel('Callback Path:')).toHaveValue('/signin-oidc')
      await expect(page.getByLabel('Groups Claim Type:')).toHaveValue('e2e-groups-claim')
      await page.getByRole('button', { name: 'Cancel' }).click()
      await expect(page).toHaveURL(/\/admin\/identity-providers$/)
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

    // D-124 Phase 4 (partial conversion): "Add Mapping" moved off this
    // page's own inline form onto GroupRoleMappingCreate.vue's routed page
    // (/admin/group-role-mapping/new) -- delete-only per row (unchanged)
    // and the Lookup/Test Tool below (unchanged) both stay on this page.
    await page.getByRole('button', { name: '+ Add Mapping' }).click()
    await expect(page).toHaveURL(/\/admin\/group-role-mapping\/new$/)
    await page.getByLabel(/^Group Name/).fill('BUILTIN\\Users')
    // D-93-adjacent fix: Role is now a real <select> populated from
    // GET /api/admin/group-role-mappings/roles, not free text -- confirms
    // the admin picks an actual existing role rather than typing one.
    await page.getByLabel('Role:').selectOption('Viewer')
    await page.getByRole('button', { name: 'Add' }).click()
    await expect(page).toHaveURL(/\/admin\/group-role-mapping$/)

    const row = page.locator('tbody tr', { hasText: builtinUsersSid })
    await expect(row).toBeVisible()
    await row.getByRole('button', { name: 'Delete' }).click()
    await expect(row).toHaveCount(0)

    // D-113 replaced this field's bare placeholder with a real <label> (an accessibility fix) -- this test wasn't updated to match at the time.
    // exact: true -- the Add Mapping form above has its own similarly-labeled "Group Name (e.g. BUILTIN\...)" field.
    await page.getByLabel('Group name', { exact: true }).fill('BUILTIN\\Users')
    await page.getByRole('button', { name: 'Resolve' }).click()
    await expect(page.getByText(/Resolved to:.*S-1-/)).toBeVisible()
  })
})

// D-124 Phase 4: Add/Edit moved off this page's own inline form onto
// RoleEdit.vue's routed pages (/admin/roles-permissions/new,
// /admin/roles-permissions/:appRoleKey).
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
    await expect(page).toHaveURL(/\/admin\/roles-permissions$/)

    const row = page.locator('tbody tr', { hasText: roleName })
    await expect(row).toBeVisible()
    await expect(row).toContainText('ViewDashboard')

    await row.getByRole('button', { name: 'Delete' }).click()
    await expect(row).toHaveCount(0)
  })
})

// D-124 Phase 4: Add/Edit moved off this page's Applications section's own
// inline form onto ApplicationEdit.vue's routed pages
// (/admin/application-mapping/new, /admin/application-mapping/:applicationKey)
// -- the Safes section below (a plain per-row <select>, not a form) is untouched.
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
    await expect(page).toHaveURL(/\/admin\/application-mapping$/)
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

// D-124 Phase 4: Add/Edit moved off this page's own inline form onto
// FieldMetadataEdit.vue's routed pages (/admin/field-metadata/new,
// /admin/field-metadata/:fieldMetadataKey).
test.describe('Field Metadata Management admin page', () => {
  test('Admin can create, edit, and delete a field definition', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/field-metadata')
    const fieldName = `E2ETestField${Date.now()}`

    await page.getByRole('button', { name: '+ New Field' }).click()
    await page.getByLabel('Field Name:').fill(fieldName)
    await page.getByLabel('Display Label:').fill('E2E Test Field Label')
    await page.locator('form button[type="submit"]').click()
    await expect(page).toHaveURL(/\/admin\/field-metadata$/)

    const row = page.locator('tbody tr', { hasText: fieldName })
    await expect(row).toBeVisible()

    await row.getByRole('link', { name: 'Edit' }).click()
    await expect(page.getByLabel('Field Name:')).toHaveValue(fieldName)
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

test.describe('Deployment admin page', () => {
  test('Admin can load environment info, health checks, and backup status', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/deployment')

    await expect(page.getByText('Loading...')).toHaveCount(0)
    await expect(page.getByRole('heading', { name: 'Environment' })).toBeVisible()
    await expect(page.getByText('Environment name')).toBeVisible()
    await expect(page.getByText('Version')).toBeVisible()

    await expect(page.getByRole('heading', { name: 'Health Checks' })).toBeVisible()
    await expect(page.getByRole('cell', { name: 'SQL Server', exact: true })).toBeVisible()
    await expect(page.getByRole('cell', { name: 'Secrets Store', exact: true })).toBeVisible()
    await expect(page.getByRole('cell', { name: 'Identity Providers', exact: true })).toBeVisible()

    await expect(page.getByRole('heading', { name: 'SQL Server Backup Status' })).toBeVisible()

    // D-117: the button itself is exercised for real by
    // DeploymentBackupTests.cs (a real BACKUP DATABASE against
    // BlueTrackTest on every run) -- not re-clicked here too, to avoid
    // writing a second real .bak file on every E2E pass as well.
    await expect(page.getByRole('button', { name: 'Backup App' })).toBeVisible()
  })

  test('A user without ViewDeploymentInfo is denied with a plain error', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/admin/deployment')

    await expect(page.getByText(/Request failed: 403/)).toBeVisible()
  })
})

test.describe('Credentials & LDAP admin page', () => {
  test('Admin can create a DPAPI credential, see it upgrade scope on Test, then delete it', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/credentials')
    const credentialName = `E2ETestCred${Date.now()}`

    await page.getByRole('button', { name: '+ New Credential' }).click()
    await page.getByLabel('Name:', { exact: true }).fill(credentialName)
    // Backend defaults to WindowsDpapi -- Username/Password/Scope fields are already visible.
    await page.getByLabel('Username:').fill('e2e-test-user')
    await page.getByLabel('Password:').fill('e2e-test-password')
    await page.getByLabel('DPAPI Scope:').selectOption('User')
    // Scoped by its own Cancel button -- the LDAP Configuration form below also has a submit button on this same page.
    await page.locator('form', { has: page.getByRole('button', { name: 'Cancel' }) }).getByRole('button', { name: 'Save' }).click()

    const row = page.locator('tbody tr', { hasText: credentialName })
    await expect(row).toBeVisible()
    await expect(row).toContainText('Machine') // starts Machine even though ScopePreference is User

    await row.getByRole('button', { name: 'Test' }).click()
    await expect(row.getByText(/OK \(e2e-test-user\)/)).toBeVisible()
    await expect(row).toContainText('User') // upgraded after the first real decrypt

    await row.getByRole('button', { name: 'Delete' }).click()
    await expect(page.locator('tbody tr', { hasText: credentialName })).toHaveCount(0)
  })

  test('Admin can enable LDAP with a trusted connection, then restore disabled default', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/credentials')

    await page.getByLabel('Enabled').check()
    await page.getByLabel('Use trusted connection (app pool / local computer account)').check()
    // Bind Account Credential picker hides once trusted connection is checked.
    await expect(page.getByLabel('Bind Account Credential:')).toHaveCount(0)
    await page.locator('form', { has: page.getByRole('button', { name: 'Save' }) }).last().getByRole('button', { name: 'Save' }).click()

    await page.reload()
    await expect(page.getByLabel('Enabled')).toBeChecked()
    await expect(page.getByLabel('Use trusted connection (app pool / local computer account)')).toBeChecked()

    await page.getByLabel('Enabled').uncheck()
    await page.getByLabel('Use trusted connection (app pool / local computer account)').uncheck()
    await page.locator('form', { has: page.getByRole('button', { name: 'Save' }) }).last().getByRole('button', { name: 'Save' }).click()
  })

  test('A user without ManageCredentials is denied with a plain error', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/admin/credentials')

    // Credentials.vue's own error text is "Credentials request failed: 403", not the generic "Request failed: 403" other pages use.
    await expect(page.getByText(/request failed: 403/)).toBeVisible()
  })
})

test.describe('Notifications admin page', () => {
  test('Admin can save SMTP config with the TLS override checkboxes, and assign a notification type target role', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/notifications')

    await page.getByLabel('SMTP Host:').fill('smtp.e2etest.local')
    await page.getByLabel('Ignore CRL issues (skip certificate revocation checking)').check()
    await page.locator('form', { has: page.getByLabel('SMTP Host:') }).getByRole('button', { name: 'Save' }).click()
    await expect(page.getByText('Saved.')).toBeVisible()

    await page.reload()
    await expect(page.getByLabel('SMTP Host:')).toHaveValue('smtp.e2etest.local')
    await expect(page.getByLabel('Ignore CRL issues (skip certificate revocation checking)')).toBeChecked()

    // D-116: additive role targeting -- assign then clear, leaving the flat-list-only default restored.
    const typeRow = page.locator('tbody tr', { hasText: 'DevFakeAuthEnabledTooLong' })
    await typeRow.getByRole('combobox').selectOption('Admin')
    await expect(typeRow.getByRole('combobox')).toHaveValue(/./)
    await typeRow.getByRole('combobox').selectOption({ label: '(none -- flat list only)' })

    // Restore SMTP config to a clean/unconfigured state.
    await page.getByLabel('SMTP Host:').fill('')
    await page.getByLabel('Ignore CRL issues (skip certificate revocation checking)').uncheck()
    await page.locator('form', { has: page.getByLabel('SMTP Host:') }).getByRole('button', { name: 'Save' }).click()
  })

  // D-124 Phase 4 (partial conversion): "Add Recipient" moved off this
  // page's own inline form onto NotificationRecipientCreate.vue's routed
  // page (/admin/notifications/recipients/new) -- recipients otherwise
  // still only toggle active/delete in place here, unchanged.
  test('Admin can create, deactivate, and delete a recipient', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/notifications')
    const email = `e2etest${Date.now()}@example.com`

    await page.getByRole('button', { name: '+ Add Recipient' }).click()
    await expect(page).toHaveURL(/\/admin\/notifications\/recipients\/new$/)
    await page.getByLabel('Email:').fill(email)
    await page.getByRole('button', { name: 'Add Recipient' }).click()
    await expect(page).toHaveURL(/\/admin\/notifications$/)

    const row = page.locator('tbody tr', { hasText: email })
    await expect(row).toBeVisible()
    await expect(row).toContainText('Yes')

    await row.getByRole('button', { name: 'Deactivate' }).click()
    await expect(page.locator('tbody tr', { hasText: email })).toContainText('No')

    await page.locator('tbody tr', { hasText: email }).getByRole('button', { name: 'Delete' }).click()
    await expect(page.locator('tbody tr', { hasText: email })).toHaveCount(0)
  })

  test('A user without ManageNotifications is denied with a plain error', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/admin/notifications')

    // Notifications.vue's own error text is "Config request failed: 403", not the generic "Request failed: 403" other pages use.
    await expect(page.getByText(/request failed: 403/)).toBeVisible()
  })
})

// D-120: named bands over the computed EffectiveRiskScore, NOT
// dbo.dim_risk_level -- confirmed by reading RiskScoreBands.vue/
// RiskScoreBandsController.cs before writing these, same as every other
// describe block in this file.
// D-124 Phase 4: Add/Edit moved off this page's own inline form onto
// RiskScoreBandEdit.vue's routed pages (/admin/risk-score-bands/new,
// /admin/risk-score-bands/:riskScoreBandKey).
test.describe('Risk Score Bands admin page', () => {
  test('Admin can create a band, see the overlap validation reject it, then edit and delete it', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin/risk-score-bands')
    const bandName = `E2ETestBand${Date.now()}`

    await page.getByRole('button', { name: '+ New Band' }).click()
    await page.getByLabel('Name:').fill(bandName)
    await page.getByLabel('Min Score:').fill('10000')
    await page.getByLabel('Max Score:').fill('10100')
    await page.getByLabel('Risk Order:').fill('9001')
    await page.locator('form button[type="submit"]').click()
    await expect(page).toHaveURL(/\/admin\/risk-score-bands$/)

    const row = page.locator('tbody tr', { hasText: bandName })
    await expect(row).toBeVisible()

    try {
      // A second band overlapping the first's range is rejected with the
      // server's own overlap message, not silently accepted.
      await page.getByRole('button', { name: '+ New Band' }).click()
      const overlappingName = `${bandName}Overlap`
      await page.getByLabel('Name:').fill(overlappingName)
      await page.getByLabel('Min Score:').fill('10050')
      await page.getByLabel('Max Score:').fill('10150')
      await page.getByLabel('Risk Order:').fill('9002')
      await page.locator('form button[type="submit"]').click()
      await expect(page.getByText(/overlaps existing band/)).toBeVisible()
      await page.getByRole('button', { name: 'Cancel' }).click()
      await expect(page).toHaveURL(/\/admin\/risk-score-bands$/)

      await row.getByRole('link', { name: 'Edit' }).click()
      const updatedName = `${bandName} (Updated)`
      await expect(page.getByLabel('Name:')).toHaveValue(bandName)
      await page.getByLabel('Name:').fill(updatedName)
      await page.locator('form button[type="submit"]').click()
      await expect(page.locator('tbody tr', { hasText: updatedName })).toBeVisible()

      await page.locator('tbody tr', { hasText: updatedName }).getByRole('button', { name: 'Delete' }).click()
      await expect(page.locator('tbody tr', { hasText: updatedName })).toHaveCount(0)
    } catch (err) {
      // Best-effort cleanup if an assertion above failed partway through.
      const leftover = page.locator('tbody tr', { hasText: bandName })
      if (await leftover.count() > 0) await leftover.getByRole('button', { name: 'Delete' }).click()
      throw err
    }
  })

  test('A user without ManageRiskScoreBands is denied with a plain error', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/admin/risk-score-bands')

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
