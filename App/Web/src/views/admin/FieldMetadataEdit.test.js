import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import FieldMetadataEdit from './FieldMetadataEdit.vue'

// UI review W11-2: Field Type and Reference Table are choices, not free text.
const push = vi.hoisted(() => vi.fn())
vi.mock('vue-router', () => ({ useRouter: () => ({ push }) }))

function json(body) { return { ok: true, status: 200, json: () => Promise.resolve(body) } }

function mockApi({ referenceData = { dim_risk_level: [], dim_application: [] }, post } = {}) {
  globalThis.fetch = vi.fn((url, init) => {
    if (init?.method === 'POST') { post?.(JSON.parse(init.body)); return Promise.resolve(json({})) }
    if (url === '/api/account-progress/reference-data') return Promise.resolve(referenceData === null ? { ok: false, status: 403 } : json(referenceData))
    return Promise.resolve(json([]))
  })
}
const field = (wrapper, label) => wrapper.findAll('label').find(l => l.text().startsWith(label))

describe('FieldMetadataEdit field type and reference table', () => {
  afterEach(() => vi.restoreAllMocks())

  it('offers the field types, and the reference tables only for a Dropdown', async () => {
    mockApi()
    const wrapper = mount(FieldMetadataEdit)
    await flushPromises()

    expect(field(wrapper, 'Field Type').findAll('option').map(o => o.text())).toEqual(['Text', 'TextArea', 'Dropdown', 'Date'])
    expect(field(wrapper, 'Reference Table')).toBeUndefined()

    await field(wrapper, 'Field Type').find('select').setValue('Dropdown')
    expect(field(wrapper, 'Reference Table').findAll('option').map(o => o.text())).toEqual(['Choose a table', 'dim_application', 'dim_risk_level'])
  })

  it('sends the chosen table for a Dropdown and none for another type', async () => {
    const posts = []
    mockApi({ post: body => posts.push(body) })
    const wrapper = mount(FieldMetadataEdit)
    await flushPromises()
    await field(wrapper, 'Field Name').find('input').setValue('RiskLevelKey')
    await field(wrapper, 'Display Label').find('input').setValue('Risk Level')
    await field(wrapper, 'Field Type').find('select').setValue('Dropdown')
    await field(wrapper, 'Reference Table').find('select').setValue('dim_risk_level')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(posts[0]).toMatchObject({ fieldType: 'Dropdown', referenceTable: 'dim_risk_level' })

    await field(wrapper, 'Field Type').find('select').setValue('Text')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(posts[1]).toMatchObject({ fieldType: 'Text', referenceTable: '' })
  })

  it('falls back to a text box for the table when the list cannot be read', async () => {
    mockApi({ referenceData: null })
    const wrapper = mount(FieldMetadataEdit)
    await flushPromises()
    await field(wrapper, 'Field Type').find('select').setValue('Dropdown')
    expect(field(wrapper, 'Reference Table').find('input').exists()).toBe(true)
  })
})
