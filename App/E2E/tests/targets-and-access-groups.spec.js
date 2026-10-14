import { test, expect } from '@playwright/test'
import { signInAs } from './auth.js'
import { confirmDelete } from './confirmDialog.js'

// Layer 4 (Design_Testing-Strategy.md): D-121 promoted Targets/Access
// Groups out of the Admin hub to their own top-level nav entries and
// granted Analyst full parity with Admin on ManageTargets/
// ManageAccessGroups -- this is the first E2E coverage for either page.
// Confirmed by reading App.vue/AdminHub.vue/Targets.vue/AccessGroups.vue
// before writing these: the top nav gates each link on its own permission
// (rights.hasPermission), and both pages now have real filter dropdowns
// plus the shared "Showing N of M total" count summary
// (FilterCountSummary/useTotalCount) that didn't exist on any page before
// this work.

test.describe('Targets/Access Groups are top-level nav entries, gated per permission', () => {
  test('Analyst sees both Targets and Access Groups in the top nav (full parity with Admin)', async ({ page }) => {
    await signInAs(page, 'TestUser.Analyst')
    await page.goto('/')

    const topNav = page.locator('nav.top-nav')
    await expect(topNav.getByRole('link', { name: 'Targets' })).toBeVisible()
    await expect(topNav.getByRole('link', { name: 'Access Groups' })).toBeVisible()
  })

  test('Viewer sees neither -- Viewer holds neither ManageTargets nor ManageAccessGroups', async ({ page }) => {
    await signInAs(page, 'TestUser.Viewer')
    await page.goto('/')

    const topNav = page.locator('nav.top-nav')
    await expect(topNav.getByRole('link', { name: 'Targets' })).toHaveCount(0)
    await expect(topNav.getByRole('link', { name: 'Access Groups' })).toHaveCount(0)
  })

  test('Targets/Access Groups no longer appear in the Admin hub sidebar (moved out, not duplicated)', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    await page.goto('/admin')

    const adminNav = page.locator('nav.admin-subnav')
    await expect(adminNav.getByRole('link', { name: 'Targets', exact: true })).toHaveCount(0)
    await expect(adminNav.getByRole('link', { name: 'Access Groups', exact: true })).toHaveCount(0)
    // The two admin-only sub-pages stay put, unmoved.
    await expect(adminNav.getByRole('link', { name: 'Target Match Review' })).toBeVisible()
    await expect(adminNav.getByRole('link', { name: 'Import Mapping Profiles' })).toBeVisible()
  })
})

test.describe('Targets page', () => {
  // D-124 Phase 4: Add/Edit moved off this page's own inline form onto
  // TargetEdit.vue's routed pages (/targets/new, /targets/:targetKey) --
  // confirms the whole navigate-fill-save-return round trip, not just that
  // the form itself still submits.
  test('Analyst can add a Target, see it in the filtered list, and the count summary reflects it', async ({ page }) => {
    await signInAs(page, 'TestUser.Analyst')
    await page.goto('/targets')

    await expect(page.getByRole('heading', { name: 'Targets' })).toBeVisible()
    await expect(page.getByText('Loading...')).toHaveCount(0)

    const totalBefore = await readTotalCount(page)

    const targetName = `E2E Test Target ${Date.now()}`
    await page.getByRole('button', { name: '+ New Target' }).click()
    await expect(page).toHaveURL(/\/targets\/new$/)
    await expect(page.getByRole('heading', { name: 'New Target' })).toBeVisible()
    await page.getByLabel('Name:').fill(targetName)
    await page.getByLabel('Type:').selectOption('Server')
    await page.getByLabel('Risk Score (0-1000):').fill('250')
    await page.locator('form button[type="submit"]').click()
    await expect(page).toHaveURL(/\/targets$/)

    const row = page.locator('tbody tr', { hasText: targetName })
    await expect(row).toBeVisible()

    // Count summary tracks the grand total, not just the filtered view (D-121).
    // Found 2026-09-23: this regex matched an older, simpler format --
    // FilterCountSummary.vue actually renders "Showing X-Y of M matching
    // (N total)" (the richer format added alongside D-121's own filtering),
    // so this locator never matched anything, timing out every run
    // regardless of environment -- not flakiness, a stale test.
    await expect(page.getByText(/matching \(\d+ total\)/)).toBeVisible()
    const totalAfter = await readTotalCount(page)
    expect(totalAfter).toBe(totalBefore + 1)

    // Filtering by a type that excludes the new row narrows the visible
    // rows without changing the grand total (X-Total-Count ignores filters).
    await page.getByLabel('Type:').selectOption('Database')
    await expect(page.locator('tbody tr', { hasText: targetName })).toHaveCount(0)
    expect(await readTotalCount(page)).toBe(totalAfter)
    await page.getByLabel('Type:').selectOption('')

    await expect(page.locator('tbody tr', { hasText: targetName })).toBeVisible()
    // D-125: clicking the Name is the edit action -- there's no separate "Edit" link.
    await row.getByRole('link', { name: targetName }).click()
    await expect(page.getByRole('heading', { name: 'Edit Target' })).toBeVisible()
    // Confirms the edit page actually pre-fetched and pre-filled this
    // specific row (a fresh navigation, not the already-loaded list item).
    await expect(page.getByLabel('Name:')).toHaveValue(targetName)
    const updatedName = `${targetName} (Updated)`
    await page.getByLabel('Name:').fill(updatedName)
    await page.locator('form button[type="submit"]').click()
    await expect(page).toHaveURL(/\/targets$/)
    await expect(page.locator('tbody tr', { hasText: updatedName })).toBeVisible()

    await page.locator('tbody tr', { hasText: updatedName }).getByRole('button', { name: 'Delete' }).click()
    await confirmDelete(page)
    await expect(page.locator('tbody tr', { hasText: updatedName })).toHaveCount(0)
  })
})

test.describe('Access Groups page', () => {
  // D-124 Phase 4: Add/Edit moved off this page's own inline form onto
  // AccessGroupEdit.vue's routed pages (/access-groups/new,
  // /access-groups/:accessGroupKey).
  test('Analyst can add an Access Group with SOR Type/SOR Address and see Discovery Source in the table', async ({ page }) => {
    await signInAs(page, 'TestUser.Analyst')
    await page.goto('/access-groups')

    await expect(page.getByRole('heading', { name: 'Access Groups' })).toBeVisible()
    await expect(page.getByText('Loading...')).toHaveCount(0)

    const groupName = `E2E Test Group ${Date.now()}`
    await page.getByRole('button', { name: '+ New Access Group' }).click()
    await expect(page).toHaveURL(/\/access-groups\/new$/)
    await expect(page.getByRole('heading', { name: 'New Access Group' })).toBeVisible()
    await page.getByLabel('Name:').fill(groupName)
    await page.getByLabel('Identifier (e.g. AD SID/DN):').fill(`CN=${groupName}`)
    await page.getByLabel('SOR Type:').selectOption('Domain')
    await page.getByLabel('SOR Address:').fill('e2e.example.com')
    await page.getByLabel('Base Risk Score (0-1000):').fill('150')
    await page.getByLabel('Discovery Source:').fill('E2E Test')
    await page.locator('form button[type="submit"]').click()
    await expect(page).toHaveURL(/\/access-groups$/)

    const row = page.locator('tbody tr', { hasText: groupName })
    await expect(row).toBeVisible()
    await expect(row).toContainText('Domain')
    await expect(row).toContainText('e2e.example.com')
    await expect(row).toContainText('E2E Test')

    // D-125: clicking the Name is the edit action -- there's no separate "Edit" link.
    await row.getByRole('link', { name: groupName }).click()
    await expect(page.getByRole('heading', { name: 'Edit Access Group' })).toBeVisible()
    await expect(page.getByLabel('SOR Address:')).toHaveValue('e2e.example.com')
    await page.getByRole('button', { name: 'Cancel' }).click()
    await expect(page).toHaveURL(/\/access-groups$/)

    await page.locator('tbody tr', { hasText: groupName }).getByRole('button', { name: 'Delete' }).click()
    await confirmDelete(page)
    await expect(page.locator('tbody tr', { hasText: groupName })).toHaveCount(0)
  })
})

/** Reads FilterCountSummary.vue's "Showing X-Y of M matching (N total)" summary's N -- assumes it's already visible. */
async function readTotalCount(page) {
  const text = await page.getByText(/matching \(\d+ total\)/).textContent()
  return Number(text.match(/\((\d+) total\)/)[1])
}

test.describe('Bulk actions on Targets (D-190)', () => {
  test('Analyst selects two targets, bulk edits their risk score, then bulk deletes them with a reason', async ({ page }) => {
    await signInAs(page, 'TestUser.Analyst')
    const tag = `E2EBulk${Date.now()}`
    const types = await (await page.request.get('/api/admin/targets/target-types')).json()
    const serverTypeKey = types.find(t => t.typeCode === 'Server').targetTypeKey
    const keys = []
    for (const n of [1, 2]) {
      const created = await (await page.request.post('/api/admin/targets', {
        data: { targetTypeKey: serverTypeKey, targetName: `${tag}-${n}`, riskScore: 100, identifiers: [] }
      })).json()
      keys.push(created.targetKey)
    }

    try {
      await page.goto('/targets')
      await page.getByLabel('Select targets for bulk actions').check()
      await page.getByLabel(`Select ${tag}-1`).check()
      await page.getByLabel(`Select ${tag}-2`).check()
      await expect(page.locator('.selection-bar')).toContainText('2 selected')

      await page.getByRole('button', { name: 'Bulk edit…' }).click()
      await page.getByLabel('Change Risk Score').check()
      await page.getByLabel('Risk Score', { exact: true }).fill('640')
      await page.getByRole('button', { name: 'Apply to 2' }).click()
      await expect(page.getByText('2 updated, 0 skipped.')).toBeVisible()
      const target = await (await page.request.get(`/api/admin/targets/${keys[0]}`)).json()
      expect(target.riskScore).toBe(640)

      // The selection was cleared (nothing skipped), so select them again.
      await page.getByLabel(`Select ${tag}-1`).check()
      await page.getByLabel(`Select ${tag}-2`).check()
      await page.getByRole('button', { name: 'Delete…' }).click()
      await page.getByLabel(/Reason to delete 2 target/).fill('E2E: retired')
      await page.locator('form.bulk-panel').getByRole('button', { name: 'Delete', exact: true }).click()
      await expect(page.getByText('2 deleted, 0 skipped.')).toBeVisible()
      expect((await page.request.get(`/api/admin/targets/${keys[0]}`)).status()).toBe(404)
    } finally {
      for (const key of keys) await page.request.delete(`/api/admin/targets/${key}`)
    }
  })
})

// D-225: Target Match Review's link target is picked with a typeahead (D-229
// replaced Merged with Link), and import mapping profiles can be pre-filled
// from a sample CSV's headers.
test.describe('Target Match Review typeahead and CSV header detection (D-225)', () => {
  async function serverTypeKey(page) {
    const types = await (await page.request.get('/api/admin/targets/target-types')).json()
    return types.find(t => t.typeCode === 'Server').targetTypeKey
  }

  async function createTarget(page, name, identifiers) {
    const response = await page.request.post('/api/admin/targets', {
      data: { targetTypeKey: await serverTypeKey(page), targetName: name, riskScore: 100, identifiers }
    })
    expect(response.status()).toBe(201)
    return (await response.json()).targetKey
  }

  test('Admin links a review row to a target found by typing part of its hostname', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    const stamp = Date.now()
    const ip = `10.94.${Math.floor(Math.random() * 250) + 1}.${Math.floor(Math.random() * 250) + 1}`
    const destName = `E2E Merge Dest ${stamp}`
    const incomingName = `E2E Incoming ${stamp}`
    const candidate = await createTarget(page, `E2E Merge Candidate ${stamp}`, [{ identifierType: 'IPAddress', identifierValue: ip }])
    const dest = await createTarget(page, destName, [{ identifierType: 'Hostname', identifierValue: `e2e-merge-${stamp}` }])
    let linked = null
    try {
      // An inventory row matching only that IP address goes to the review queue.
      const upload = await page.request.post('/api/admin/risk-scoring/import/target-inventory', {
        multipart: { file: { name: 'review.csv', mimeType: 'text/csv', buffer: Buffer.from(`TargetType,TargetName,RiskScore,IPAddress\nServer,${incomingName},10,${ip}\n`) } }
      })
      expect((await upload.json()).pendingReviewCount).toBe(1)

      await page.goto('/admin/target-match-review')
      await expect(page.getByRole('heading', { name: 'Target Match Review' })).toBeVisible()
      const combobox = page.getByRole('combobox', { name: `Link IPAddress ${ip} to target` })
      await expect(combobox).toHaveAttribute('aria-expanded', 'false')

      await combobox.pressSequentially(`merge-${stamp}`)
      const option = page.getByRole('option', { name: new RegExp(destName) })
      await expect(option).toBeVisible()
      await expect(option).toContainText(`Hostname: e2e-merge-${stamp}`)
      await expect(combobox).toHaveAttribute('aria-expanded', 'true')

      await combobox.press('ArrowDown')
      await expect(option).toHaveAttribute('aria-selected', 'true')
      await combobox.press('Enter')
      await expect(combobox).toHaveValue(`${destName} (Server)`)
      await expect(combobox).toHaveAttribute('aria-expanded', 'false')

      await page.locator('tbody tr', { hasText: ip }).getByRole('button', { name: 'Link', exact: true }).click()
      await expect(page.locator('tbody tr', { hasText: ip })).toHaveCount(0)

      // D-229: the incoming row is its own target, linked to the chosen one; the IP stays on the candidate.
      const destTarget = await (await page.request.get(`/api/admin/targets/${dest}`)).json()
      linked = destTarget.linkedTargets.find(l => l.targetName === incomingName).targetKey
      const linkedTarget = await (await page.request.get(`/api/admin/targets/${linked}`)).json()
      expect(linkedTarget.linkedToTargetKey).toBe(dest)
      const candidateTarget = await (await page.request.get(`/api/admin/targets/${candidate}`)).json()
      expect(candidateTarget.identifiers).toContainEqual({ identifierType: 'IPAddress', identifierValue: ip })
    } finally {
      if (linked) await page.request.delete(`/api/admin/targets/${linked}`)
      await page.request.delete(`/api/admin/targets/${dest}`)
      // The resolved review row still names the candidate, so its delete is refused (409);
      // it's left in BlueTrackTest under its E2E name.
      await page.request.delete(`/api/admin/targets/${candidate}`)
    }
  })

  // D-229: a linked target is hidden on Targets until "Linked targets: Show"; its page names its primary and can unlink it.
  test('Admin links a review row to the candidate, finds the linked target with the filter, and unlinks it', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    const stamp = Date.now()
    const ip = `10.92.${Math.floor(Math.random() * 250) + 1}.${Math.floor(Math.random() * 250) + 1}`
    // A name starting "000" sorts to the top of the Targets list's first page.
    const primaryName = `E2E LinkPrimary ${stamp}`
    const linkedName = `000 E2E LinkedDb ${stamp}`
    const primary = await createTarget(page, primaryName, [{ identifierType: 'IPAddress', identifierValue: ip }])
    let linked = null
    try {
      const upload = await page.request.post('/api/admin/risk-scoring/import/target-inventory', {
        multipart: { file: { name: 'link.csv', mimeType: 'text/csv', buffer: Buffer.from(`TargetType,TargetName,RiskScore,DiscoverySource,IPAddress,Hostname\nDatabase,${linkedName},300,E2E CMDB,${ip},e2e-linkdb-${stamp}\n`) } }
      })
      expect((await upload.json()).pendingReviewCount).toBe(1)

      await page.goto('/admin/target-match-review')
      const row = page.locator('tbody tr', { hasText: ip })
      await expect(row).toContainText(`${linkedName} (Database)`)
      await expect(row).toContainText('Source: E2E CMDB, Risk Score: 300')
      await expect(row).toContainText(`Hostname: e2e-linkdb-${stamp}`)
      await row.getByRole('button', { name: 'Link', exact: true }).click()
      await expect(row).toHaveCount(0)

      await page.goto('/targets')
      await expect(page.getByRole('heading', { name: 'Targets', exact: true })).toBeVisible()
      await expect(page.locator('tbody tr').first()).toBeVisible()
      await expect(page.getByRole('link', { name: linkedName, exact: true })).toHaveCount(0)

      // The select's name includes its chosen option ("Linked targets: Hide"), so match by role and prefix.
      await page.getByRole('combobox', { name: /^Linked targets:/ }).selectOption('Show')
      await expect(page.getByRole('link', { name: linkedName, exact: true })).toBeVisible()
      await expect(page.locator('tbody tr', { hasText: linkedName }).getByRole('link', { name: new RegExp(`^Linked ?: to target ${primaryName}$`) })).toBeVisible()

      await page.getByRole('link', { name: linkedName, exact: true }).click()
      await expect(page.getByRole('heading', { name: 'Edit Target' })).toBeVisible()
      await expect(page.getByRole('heading', { name: 'Linked Target', exact: true })).toBeVisible()
      linked = Number(new URL(page.url()).pathname.split('/').pop())
      await page.getByRole('button', { name: 'Unlink', exact: true }).click()
      await confirmDelete(page, 'Unlink')
      await expect(page.getByRole('heading', { name: 'Linked Target', exact: true })).toHaveCount(0)

      // Unlinked, it's an ordinary target again: listed without the filter.
      await page.goto('/targets')
      await expect(page.getByRole('link', { name: linkedName, exact: true })).toBeVisible()
    } finally {
      if (linked) await page.request.delete(`/api/admin/targets/${linked}`)
      // The resolved review row still names the primary, so its delete is refused (409).
      await page.request.delete(`/api/admin/targets/${primary}`)
    }
  })

  test('Admin pre-fills a new mapping profile from a sample CSV without overwriting a field already set', async ({ page }) => {
    await signInAs(page, 'TestUser.Admin')
    const profileName = `E2E Detect ${Date.now()}`
    await page.goto('/admin/import-mapping-profiles/new')
    await expect(page.getByRole('heading', { name: 'New Profile' })).toBeVisible()
    await page.getByLabel('Profile Name:').fill(profileName)
    await page.getByRole('button', { name: '+ Add Field' }).click()
    await page.getByLabel('Source Column:').first().fill('Criticality')
    await page.getByLabel('Internal Field:').first().fill('RiskScore')

    // Semicolon-separated, quoted headers, with a UTF-8 byte order mark.
    await page.getByLabel('Sample CSV file:').setInputFiles({
      name: 'cmdb.csv', mimeType: 'text/csv', buffer: Buffer.from('﻿"Name";"Kind";"Score";"DNS Name";"Owner"\nweb01;Server;5;web01.example.test;ops\n')
    })
    await expect(page.getByText('Found 5 columns (semicolon-separated).')).toBeVisible()
    await expect(page.getByText('Columns not used: Owner.')).toBeVisible()
    await expect(page.getByText('RiskScore: now Criticality, the file has Score')).toBeVisible()

    const internal = page.getByLabel('Internal Field:')
    const source = page.getByLabel('Source Column:')
    await expect(internal).toHaveCount(4)
    await expect(source.nth(0)).toHaveValue('Criticality') // not overwritten
    await expect(internal.nth(1)).toHaveValue('TargetType')
    await expect(source.nth(1)).toHaveValue('Kind')
    await expect(internal.nth(2)).toHaveValue('TargetName')
    await expect(source.nth(2)).toHaveValue('Name')
    await expect(internal.nth(3)).toHaveValue('FQDN')
    await expect(source.nth(3)).toHaveValue('DNS Name')

    await page.getByRole('button', { name: 'Replace with the file\'s columns' }).click()
    await expect(source.nth(0)).toHaveValue('Score')

    await page.locator('form button[type="submit"]').click()
    await expect(page).toHaveURL(/\/admin\/import-mapping-profiles$/)
    const profiles = await (await page.request.get('/api/admin/risk-scoring/import-mapping-profiles')).json()
    const saved = profiles.find(p => p.profileName === profileName)
    try {
      expect(Object.fromEntries(saved.fields.map(f => [f.targetFieldName, f.sourceColumnName])))
        .toEqual({ RiskScore: 'Score', TargetType: 'Kind', TargetName: 'Name', FQDN: 'DNS Name' })
    } finally {
      await page.request.delete(`/api/admin/risk-scoring/import-mapping-profiles/${saved.importMappingProfileKey}`)
    }
  })

  test('A Bulk Actions upload shows its missing columns and opens a profile pre-filled from them', async ({ page }) => {
    await signInAs(page, 'TestUser.Analyst')
    await page.goto('/targets/bulk-import')
    await expect(page.getByRole('heading', { name: 'Targets: Bulk Actions' })).toBeVisible()

    await page.getByLabel('Target inventory CSV file').setInputFiles({
      name: 'export.csv', mimeType: 'text/csv', buffer: Buffer.from('Name\tKind\tScore\tHostname\nweb01\tServer\t5\tweb01\n')
    })
    await expect(page.getByText('Missing: TargetType, TargetName, RiskScore.')).toBeVisible()
    await expect(page.getByText('Read by name: Hostname.')).toBeVisible()
    await page.getByRole('button', { name: 'Create a mapping profile from these columns' }).click()

    await expect(page).toHaveURL(/\/admin\/import-mapping-profiles\/new$/)
    await expect(page.getByRole('heading', { name: 'New Profile' })).toBeVisible()
    await expect(page.getByText('Found 4 columns.')).toBeVisible()
    await expect(page.getByLabel('Internal Field:')).toHaveCount(4)
    await expect(page.getByLabel('Source Column:').first()).toHaveValue('Kind')
  })
})
