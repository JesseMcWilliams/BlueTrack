import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import AccessGroupsBulkImport from './AccessGroupsBulkImport.vue'

// D-124 Phase 5: the 3 always-visible inline "Bulk Import" sections moved
// off AccessGroups.vue onto this dedicated page -- this test file
// establishes coverage for them here (AccessGroups.test.js itself never had
// any bulk-import test coverage to move -- see this phase's design register
// note), following TargetEdit.test.js's own globalThis.fetch mocking style
// and TargetsBulkImport.test.js's own file-input testing convention.
function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, json: () => Promise.resolve(body) }
}

// File inputs are read-only in the DOM -- setValue() doesn't work on them,
// so tests instead stub the input element's own `files` property directly
// before dispatching the change event the component's onChange handler
// listens for.
async function selectFile(input, file) {
  Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
  await input.trigger('change')
}

describe('AccessGroupsBulkImport.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('renders all 3 bulk import sections with their Import buttons disabled until a file is chosen', () => {
    const wrapper = mount(AccessGroupsBulkImport)

    expect(wrapper.text()).toContain('Bulk Import: Access Group Inventory')
    expect(wrapper.text()).toContain('Bulk Import: Access Group -> Target Map')
    expect(wrapper.text()).toContain('Bulk Import: Account -> Access Group Membership')
    const buttons = wrapper.findAll('button')
    expect(buttons).toHaveLength(3)
    buttons.forEach(b => expect(b.attributes('disabled')).toBeDefined())
  })

  it('Access Group Inventory: choosing a file enables Import, which POSTs to the access-group-inventory endpoint', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({ totalRows: 5, succeededCount: 5, errors: [] })))
    const wrapper = mount(AccessGroupsBulkImport)
    const file = new File(['a,b'], 'groups.csv', { type: 'text/csv' })

    await selectFile(wrapper.findAll('input[type="file"]')[0], file)
    const importButton = wrapper.findAll('button')[0]
    expect(importButton.attributes('disabled')).toBeUndefined()

    await importButton.trigger('click')
    await flushPromises()

    const [url, options] = globalThis.fetch.mock.calls[0]
    expect(url).toBe('/api/admin/risk-scoring/import/access-group-inventory')
    expect(options.method).toBe('POST')
    expect(options.body.get('file').name).toBe(file.name)
    expect(wrapper.text()).toContain('5 rows -- 5 succeeded, 0 errors.')
  })

  it('Access Group -> Target Map: choosing a file enables Import, which POSTs to the access-group-target-map endpoint', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({ totalRows: 8, succeededCount: 7, errors: [{ rowNumber: 3, error: 'Unknown target' }] })))
    const wrapper = mount(AccessGroupsBulkImport)
    const file = new File(['a,b'], 'map.csv', { type: 'text/csv' })

    await selectFile(wrapper.findAll('input[type="file"]')[1], file)
    const importButton = wrapper.findAll('button')[1]
    expect(importButton.attributes('disabled')).toBeUndefined()

    await importButton.trigger('click')
    await flushPromises()

    const [url, options] = globalThis.fetch.mock.calls[0]
    expect(url).toBe('/api/admin/risk-scoring/import/access-group-target-map')
    expect(options.body.get('file').name).toBe(file.name)
    expect(wrapper.text()).toContain('8 rows -- 7 succeeded, 1 errors.')
    expect(wrapper.text()).toContain('Row 3: Unknown target')
  })

  it('Account -> Access Group Membership: choosing a file enables Import, which POSTs to the membership endpoint', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({ totalRows: 3, succeededCount: 3, errors: [] })))
    const wrapper = mount(AccessGroupsBulkImport)
    const file = new File(['a,b'], 'membership.csv', { type: 'text/csv' })

    await selectFile(wrapper.findAll('input[type="file"]')[2], file)
    const importButton = wrapper.findAll('button')[2]
    expect(importButton.attributes('disabled')).toBeUndefined()

    await importButton.trigger('click')
    await flushPromises()

    const [url, options] = globalThis.fetch.mock.calls[0]
    expect(url).toBe('/api/admin/risk-scoring/import/account-access-group-membership')
    expect(options.body.get('file').name).toBe(file.name)
    expect(wrapper.text()).toContain('3 rows -- 3 succeeded, 0 errors.')
  })

  // The result summary block binds directly to whatever importFile() stored
  // (unchanged from AccessGroups.vue) -- on a non-OK response that's just
  // { error: '...' }, so totalRows/succeededCount/etc render as empty rather
  // than a dedicated failure message. That gap predates this phase (a pure
  // relocation, not a rewrite) and isn't introduced or fixed here; this test
  // pins the actual current rendering rather than an aspirational one.
  it('a failed import still renders the (empty) result block rather than throwing', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse(null, { ok: false, status: 500 })))
    const wrapper = mount(AccessGroupsBulkImport)
    await selectFile(wrapper.findAll('input[type="file"]')[0], new File(['a'], 'groups.csv', { type: 'text/csv' }))

    await wrapper.findAll('button')[0].trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('rows --  succeeded, 0 errors.')
    expect(wrapper.findAll('button')[0].text()).toBe('Import')
  })
})
