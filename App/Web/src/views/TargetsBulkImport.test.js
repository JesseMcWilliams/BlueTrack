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

    const [url, options] = globalThis.fetch.mock.calls[0]
    expect(url).toBe('/api/admin/risk-scoring/import/target-inventory')
    expect(options.method).toBe('POST')
    expect(options.body.get('file').name).toBe(file.name)
    expect(wrapper.text()).toContain('10 rows -- 6 created, 3 merged, 1 pending review, 0 errors.')
  })

  // The result summary block binds directly to whatever importFile() stored
  // (unchanged from Targets.vue) -- on a non-OK response that's just
  // { error: '...' }, so totalRows/createdCount/etc render as empty rather
  // than a dedicated failure message. That gap predates this phase (a pure
  // relocation, not a rewrite) and isn't introduced or fixed here; this test
  // pins the actual current rendering rather than an aspirational one.
  it('Target Inventory: a failed import still renders the (empty) result block rather than throwing', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse(null, { ok: false, status: 500 })))
    const wrapper = mount(TargetsBulkImport)
    await selectFile(wrapper.findAll('input[type="file"]')[0], new File(['a'], 'targets.csv', { type: 'text/csv' }))

    await wrapper.findAll('button')[0].trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('rows --  created,  merged,  pending review, 0 errors.')
    expect(wrapper.findAll('button')[0].text()).toBe('Import') // importing reset back to false
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

    const [url, options] = globalThis.fetch.mock.calls[0]
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
