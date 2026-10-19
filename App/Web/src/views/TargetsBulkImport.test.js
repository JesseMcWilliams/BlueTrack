import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import TargetsBulkImport from './TargetsBulkImport.vue'

// D-124 Phase 5: the 2 always-visible inline "Bulk Import" sections moved
// off Targets.vue onto this dedicated page -- this test file establishes
// coverage for them here (Targets.test.js itself never had any bulk-import
// test coverage to move -- see this phase's design register note),
// following TargetEdit.test.js's own globalThis.fetch mocking style.
function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, json: () => Promise.resolve(body) }
}

// File inputs are read-only in the DOM -- setValue() doesn't work on them,
// so tests instead stub the input element's own `files` property directly
// (a common @vue/test-utils workaround) before dispatching the change event
// the component's onChange handler listens for.
async function selectFile(input, file) {
  Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
  await input.trigger('change')
}

describe('TargetsBulkImport.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('renders both bulk import sections with their Import buttons disabled until a file is chosen', () => {
    const wrapper = mount(TargetsBulkImport)

    expect(wrapper.text()).toContain('Bulk Import: Target Inventory')
    expect(wrapper.text()).toContain('Bulk Import: Direct Account -> Target Links')
    const buttons = wrapper.findAll('button')
    expect(buttons).toHaveLength(2)
    buttons.forEach(b => expect(b.attributes('disabled')).toBeDefined())
  })

  it('Target Inventory: choosing a file enables Import, which POSTs the file and renders the result summary', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({
      totalRows: 10, createdCount: 6, mergedCount: 3, pendingReviewCount: 1, errors: []
    })))
    const wrapper = mount(TargetsBulkImport)
    const file = new File(['a,b'], 'targets.csv', { type: 'text/csv' })

    await selectFile(wrapper.findAll('input[type="file"]')[0], file)
    const importButton = wrapper.findAll('button')[0]
    expect(importButton.attributes('disabled')).toBeUndefined()

    await importButton.trigger('click')
    await flushPromises()

    const [url, options] = globalThis.fetch.mock.calls.find(([, o]) => o?.method === 'POST')
    expect(url).toBe('/api/admin/risk-scoring/import/target-inventory')
    expect(options.method).toBe('POST')
    expect(options.body.get('file').name).toBe(file.name)
    expect(wrapper.text()).toContain('10 rows -- 6 created, 3 merged, 1 pending review, 0 errors.')
  })

  // D-229: rows ignored on Target Match Review before are skipped, and counted separately.
  it('Target Inventory: says how many rows were skipped as ignored before, only when some were', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({
      totalRows: 4, createdCount: 1, mergedCount: 1, pendingReviewCount: 0, ignoredCount: 2, errors: []
    })))
    const wrapper = mount(TargetsBulkImport)
    await selectFile(wrapper.findAll('input[type="file"]')[0], new File(['a,b'], 'targets.csv', { type: 'text/csv' }))
    await wrapper.findAll('button')[0].trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('4 rows -- 1 created, 1 merged, 0 pending review, 0 errors.')
    expect(wrapper.text()).toContain('2 skipped: ignored on Target Match Review before.')
  })

  // UI review W4-1: a failed import used to render a summary of blanks ("rows --  created, ...") because the
  // template never read the error importFile() stored.
  async function importWith(fetchImpl, section = 0) {
    globalThis.fetch = vi.fn(fetchImpl)
    const wrapper = mount(TargetsBulkImport)
    await selectFile(wrapper.findAll('input[type="file"]')[section], new File(['a'], 'targets.csv', { type: 'text/csv' }))
    await wrapper.findAll('button')[section].trigger('click')
    await flushPromises()
    return wrapper
  }

  it.each([0, 1])('section %i: a failed import shows the error, not a summary of blanks', async (section) => {
    const wrapper = await importWith(() => Promise.resolve(jsonResponse(null, { ok: false, status: 500 })), section)

    expect(wrapper.find('[role="alert"]').text()).toBe('Import failed: 500')
    expect(wrapper.text()).not.toContain('rows --')
    expect(wrapper.findAll('button')[section].text()).toBe('Import') // importing reset back to false
  })

  it('shows the API\'s own reason when it sent one (a 413, a bad file)', async () => {
    const wrapper = await importWith(() => Promise.resolve(jsonResponse({ detail: 'The file is larger than 5 MB.' }, { ok: false, status: 413 })))

    expect(wrapper.find('[role="alert"]').text()).toBe('The file is larger than 5 MB.')
  })

  it('says so when the server cannot be reached', async () => {
    const wrapper = await importWith(() => Promise.reject(new TypeError('Failed to fetch')))

    expect(wrapper.find('[role="alert"]').text()).toContain("the server couldn't be reached")
    expect(wrapper.findAll('button')[0].text()).toBe('Import')
  })

  it('clears an earlier error when the next import works', async () => {
    const wrapper = await importWith(() => Promise.resolve(jsonResponse(null, { ok: false, status: 500 })))
    expect(wrapper.find('[role="alert"]').exists()).toBe(true)

    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({ totalRows: 2, createdCount: 2, mergedCount: 0, pendingReviewCount: 0, errors: [] })))
    await wrapper.findAll('button')[0].trigger('click')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('2 rows -- 2 created')
  })

  it('Direct Account -> Target Links: choosing a file enables Import, which POSTs to the account-target-map endpoint', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({
      totalRows: 4, createdCount: 3, mergedCount: 1, errors: []
    })))
    const wrapper = mount(TargetsBulkImport)
    const file = new File(['a,b'], 'links.csv', { type: 'text/csv' })

    await selectFile(wrapper.findAll('input[type="file"]')[1], file)
    const importButton = wrapper.findAll('button')[1]
    expect(importButton.attributes('disabled')).toBeUndefined()

    await importButton.trigger('click')
    await flushPromises()

    const [url, options] = globalThis.fetch.mock.calls.find(([, o]) => o?.method === 'POST')
    expect(url).toBe('/api/admin/risk-scoring/import/account-target-map')
    expect(options.body.get('file').name).toBe(file.name)
    expect(wrapper.text()).toContain('4 rows -- 3 created, 1 already linked, 0 errors.')
  })

  it('reports per-row errors alongside the counts when the import partially fails', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({
      totalRows: 2, createdCount: 1, mergedCount: 0, pendingReviewCount: 0,
      errors: [{ rowNumber: 2, error: 'Unknown identifier type' }]
    })))
    const wrapper = mount(TargetsBulkImport)
    await selectFile(wrapper.findAll('input[type="file"]')[0], new File(['a'], 'targets.csv', { type: 'text/csv' }))

    await wrapper.findAll('button')[0].trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Row 2: Unknown identifier type')
  })
})

// D-207: with active profiles for the feed type, the upload offers them (None by default)
// and sends the chosen one; with none chosen, nothing extra is sent.
describe('TargetsBulkImport.vue mapping profile', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('offers the active profiles and sends the chosen one with the upload', async () => {
    globalThis.fetch = vi.fn((url, options) => {
      if (url.startsWith('/api/admin/risk-scoring/import/target-mapping-profiles?feedType=TargetInventory')) {
        return Promise.resolve(jsonResponse([{ importMappingProfileKey: 7, profileName: 'CMDB export' }]))
      }
      if (url.includes('mapping-profiles')) return Promise.resolve(jsonResponse([]))
      return Promise.resolve(jsonResponse({ totalRows: 1, createdCount: 1, mergedCount: 0, pendingReviewCount: 0, errors: [] }))
    })
    const wrapper = mount(TargetsBulkImport)
    await flushPromises()

    const select = wrapper.get('select')
    expect(select.text()).toContain('None (the template\'s own column names)')
    expect(select.text()).toContain('CMDB export')

    await wrapper.findAll('option').find(o => o.text() === 'CMDB export').setSelected()
    await selectFile(wrapper.findAll('input[type="file"]')[0], new File(['a,b'], 'cmdb.csv', { type: 'text/csv' }))
    await wrapper.findAll('button')[0].trigger('click')
    await flushPromises()

    const [, options] = globalThis.fetch.mock.calls.find(([, o]) => o?.method === 'POST')
    expect(options.body.get('mappingProfileKey')).toBe('7')
  })

  it('sends no profile when None is left selected', async () => {
    globalThis.fetch = vi.fn((url) => url.includes('mapping-profiles')
      ? Promise.resolve(jsonResponse([{ importMappingProfileKey: 7, profileName: 'CMDB export' }]))
      : Promise.resolve(jsonResponse({ totalRows: 1, createdCount: 1, mergedCount: 0, pendingReviewCount: 0, errors: [] })))
    const wrapper = mount(TargetsBulkImport)
    await flushPromises()

    await selectFile(wrapper.findAll('input[type="file"]')[0], new File(['a,b'], 'targets.csv', { type: 'text/csv' }))
    await wrapper.findAll('button')[0].trigger('click')
    await flushPromises()

    const [, options] = globalThis.fetch.mock.calls.find(([, o]) => o?.method === 'POST')
    expect(options.body.get('mappingProfileKey')).toBeNull()
  })
})
