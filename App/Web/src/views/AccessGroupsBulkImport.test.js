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

    const [url, options] = globalThis.fetch.mock.calls.find(([, o]) => o?.method === 'POST')
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

    const [url, options] = globalThis.fetch.mock.calls.find(([, o]) => o?.method === 'POST')
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

    const [url, options] = globalThis.fetch.mock.calls.find(([, o]) => o?.method === 'POST')
    expect(url).toBe('/api/admin/risk-scoring/import/account-access-group-membership')
    expect(options.body.get('file').name).toBe(file.name)
    expect(wrapper.text()).toContain('3 rows -- 3 succeeded, 0 errors.')
  })

  // UI review W4-1: a failed import used to render a summary of blanks ("rows --  succeeded, ...") because the
  // template never read the error importFile() stored.
  async function importWith(fetchImpl, section = 0) {
    globalThis.fetch = vi.fn(fetchImpl)
    const wrapper = mount(AccessGroupsBulkImport)
    await selectFile(wrapper.findAll('input[type="file"]')[section], new File(['a'], 'groups.csv', { type: 'text/csv' }))
    await wrapper.findAll('button')[section].trigger('click')
    await flushPromises()
    return wrapper
  }

  it.each([0, 1, 2])('section %i: a failed import shows the error, not a summary of blanks', async (section) => {
    const wrapper = await importWith(() => Promise.resolve(jsonResponse(null, { ok: false, status: 500 })), section)

    expect(wrapper.find('[role="alert"]').text()).toBe('Import failed: 500')
    expect(wrapper.text()).not.toContain('rows --')
    expect(wrapper.findAll('button')[section].text()).toBe('Import')
  })

  it('shows the API\'s own reason when it sent one', async () => {
    const wrapper = await importWith(() => Promise.resolve(jsonResponse({ detail: 'The file is larger than 5 MB.' }, { ok: false, status: 413 })), 1)

    expect(wrapper.find('[role="alert"]').text()).toBe('The file is larger than 5 MB.')
  })

  it('says so when the server cannot be reached', async () => {
    const wrapper = await importWith(() => Promise.reject(new TypeError('Failed to fetch')), 2)

    expect(wrapper.find('[role="alert"]').text()).toContain("the server couldn't be reached")
    expect(wrapper.findAll('button')[2].text()).toBe('Import')
  })
})
