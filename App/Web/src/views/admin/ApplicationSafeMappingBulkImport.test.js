import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ApplicationSafeMappingBulkImport from './ApplicationSafeMappingBulkImport.vue'

// D-180: the Application ↔ Safe Mapping page's CSV imports (D-239 added the third, Account → Application).
function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, json: () => Promise.resolve(body) }
}

// File inputs are read-only in the DOM -- stub `files`, then fire change
// (same workaround as TargetsBulkImport.test.js).
async function selectFile(input, file) {
  Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
  await input.trigger('change')
}

function importButtons(wrapper) {
  return wrapper.findAll('button').filter(b => b.text() === 'Import')
}

describe('ApplicationSafeMappingBulkImport.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows every import, each Import disabled until a file is chosen', () => {
    const wrapper = mount(ApplicationSafeMappingBulkImport)

    expect(wrapper.text()).toContain('Bulk Import: Applications')
    expect(wrapper.text()).toContain('Bulk Import: Safe → Application Assignments')
    expect(wrapper.text()).toContain('Bulk Import: Account → Application Assignments')
    expect(importButtons(wrapper).map(b => b.attributes('disabled'))).toEqual(['', '', ''])
  })

  it('Applications: posts the file and shows created/updated/unchanged counts and row errors', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({
      totalRows: 3, createdCount: 1, updatedCount: 1, unchangedCount: 0,
      errors: [{ rowNumber: 4, error: 'ApplicationCode is required.' }]
    })))
    const wrapper = mount(ApplicationSafeMappingBulkImport)

    await selectFile(wrapper.get('input[aria-label="Applications CSV file"]'), new File(['x'], 'apps.csv', { type: 'text/csv' }))
    await importButtons(wrapper)[0].trigger('click')
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/application-mapping/import/applications', expect.objectContaining({ method: 'POST' }))
    const summary = wrapper.get('[data-testid="applications-result"]').text()
    expect(summary).toContain('1 created, 1 updated')
    expect(summary).toContain('Row 4: ApplicationCode is required.')
  })

  it('Assignments: posts the file and shows per-safe counts; a failed request shows an error', async () => {
    globalThis.fetch = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ totalRows: 2, assignedCount: 2, changedCount: 1, unchangedCount: 0, errors: [] }))
      .mockResolvedValueOnce(jsonResponse(null, { ok: false, status: 403 }))
    const wrapper = mount(ApplicationSafeMappingBulkImport)

    await selectFile(wrapper.get('input[aria-label="Safe assignments CSV file"]'), new File(['x'], 'safes.csv', { type: 'text/csv' }))
    await importButtons(wrapper)[1].trigger('click')
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/application-mapping/import/safe-assignments', expect.objectContaining({ method: 'POST' }))
    expect(wrapper.get('[data-testid="assignments-result"]').text()).toContain('2 safes assigned, 1 changed')

    await importButtons(wrapper)[1].trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="assignments-result"]').text()).toContain('Import failed: 403')
  })

  it('Account assignments (D-239): posts the file and shows per-account counts and row errors', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({
      totalRows: 3, assignedCount: 2, changedCount: 0, unchangedCount: 0,
      errors: [{ rowNumber: 4, error: "No account with username 'x' and address 'y'." }]
    })))
    const wrapper = mount(ApplicationSafeMappingBulkImport)

    await selectFile(wrapper.get('input[aria-label="Account assignments CSV file"]'), new File(['x'], 'accounts.csv', { type: 'text/csv' }))
    await importButtons(wrapper)[2].trigger('click')
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/application-mapping/import/account-assignments', expect.objectContaining({ method: 'POST' }))
    const summary = wrapper.get('[data-testid="account-assignments-result"]').text()
    expect(summary).toContain('2 accounts assigned, 0 changed')
    expect(summary).toContain("Row 4: No account with username 'x' and address 'y'.")
  })
})

describe('ApplicationSafeMappingBulkImport.vue: failures (UI review W8-3)', () => {
  afterEach(() => vi.restoreAllMocks())

  it('lists each row error on its own line', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({
      totalRows: 3, createdCount: 1, updatedCount: 0, unchangedCount: 0,
      errors: [{ rowNumber: 4, error: 'First problem.' }, { rowNumber: 6, error: 'Second problem.' }]
    })))
    const wrapper = mount(ApplicationSafeMappingBulkImport)
    await selectFile(wrapper.get('input[aria-label="Applications CSV file"]'), new File(['x'], 'apps.csv', { type: 'text/csv' }))
    await importButtons(wrapper)[0].trigger('click')
    await flushPromises()

    expect(wrapper.findAll('[data-testid="applications-result"] li').map(li => li.text())).toEqual(['Row 4: First problem.', 'Row 6: Second problem.'])
  })

  it('says so when the server does not answer, and gives the API reason when it refuses', async () => {
    globalThis.fetch = vi.fn(() => Promise.reject(new TypeError('Failed to fetch')))
    const wrapper = mount(ApplicationSafeMappingBulkImport)
    await selectFile(wrapper.get('input[aria-label="Applications CSV file"]'), new File(['x'], 'apps.csv', { type: 'text/csv' }))
    await importButtons(wrapper)[0].trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="applications-result"]').text()).toBe('The server did not answer, so nothing was imported.')

    globalThis.fetch = vi.fn(() => Promise.resolve({ ok: false, status: 400, json: () => Promise.resolve({ detail: 'The file has no header row.' }) }))
    await importButtons(wrapper)[0].trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="applications-result"]').text()).toBe('The file has no header row.')
  })
})
