import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ApplicationSafeMappingBulkImport from './ApplicationSafeMappingBulkImport.vue'

// D-180: the Application ↔ Safe Mapping page's two CSV imports.
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

  it('shows both imports, each Import disabled until a file is chosen', () => {
    const wrapper = mount(ApplicationSafeMappingBulkImport)

    expect(wrapper.text()).toContain('Bulk Import: Applications')
    expect(wrapper.text()).toContain('Bulk Import: Safe → Application Assignments')
    expect(importButtons(wrapper).map(b => b.attributes('disabled'))).toEqual(['', ''])
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
})
