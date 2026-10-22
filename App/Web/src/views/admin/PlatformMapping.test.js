import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import PlatformMapping from './PlatformMapping.vue'

// D-247: Admin > Platform Mapping.
vi.mock('../../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../../composables/useConfirmDialog'

function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const options = {
  accountTypes: [{ key: 1, name: 'Domain Account' }, { key: 2, name: 'Local/OS Account' }],
  sourcesOfRecord: [{ key: 7, name: 'Active Directory' }, { key: 8, name: 'LDAP' }]
}

const rows = [
  { platformKey: 11, sourceSystemName: 'Self-Hosted', platformId: 'UnixSSH', platformName: 'UnixSSH', accountTypeKey: null, accountTypeName: null, sorKey: null, sourceOfRecord: null, notes: null, isUnmapped: true, accountCount: 2375, accountsWithoutType: 2375, accountsWithoutSor: 2375 },
  { platformKey: 12, sourceSystemName: 'Privilege Cloud', platformId: 'WinDomain', platformName: 'Windows Domain', accountTypeKey: 1, accountTypeName: 'Domain Account', sorKey: 7, sourceOfRecord: 'Active Directory', notes: 'AD accounts', isUnmapped: false, accountCount: 40, accountsWithoutType: 0, accountsWithoutSor: 0 }
]

function mockApi(list = rows, overrides = {}) {
  globalThis.fetch = vi.fn((url, init = {}) => {
    const key = `${init.method ?? 'GET'} ${url}`
    if (overrides[key]) return Promise.resolve(overrides[key])
    if (key === 'GET /api/admin/platform-mapping') return Promise.resolve(jsonResponse(list))
    if (key === 'GET /api/admin/platform-mapping/options') return Promise.resolve(jsonResponse(options))
    if (init.method === 'PUT') return Promise.resolve(jsonResponse({ accountsTypeFilled: 5, accountsSorFilled: 3 }))
    return Promise.resolve(jsonResponse(null, true, 204))
  })
}

// A select's option is chosen by its text (setValue with a number does not select a numeric option value).
async function pick(select, text) {
  await select.findAll('option').find(o => o.text() === text).setSelected()
}

async function mountPage(list, overrides) {
  mockApi(list, overrides)
  const wrapper = mount(PlatformMapping)
  await flushPromises()
  return wrapper
}

describe('PlatformMapping.vue', () => {
  beforeEach(() => {
    confirmDelete.mockResolvedValue(true)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('lists the platforms, unmapped first, with their counts and the unmapped total', async () => {
    const wrapper = await mountPage()

    expect(wrapper.get('h2').text()).toBe('Platform Mapping')
    expect(wrapper.text()).toContain('1 platform(s) have no mapping yet.')
    const lines = wrapper.findAll('tbody tr')
    expect(lines[0].text()).toContain('UnixSSH')
    expect(lines[0].text()).toContain('2,375')
    expect(lines[1].text()).toContain('WinDomain (Windows Domain)')
    expect(lines[1].get('select[aria-label="Account type for WinDomain"]').element.value).toBe('1')
  })

  it('filters by the search text and to unmapped platforms only', async () => {
    const wrapper = await mountPage()

    await wrapper.get('input[type="search"]').setValue('windows')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.text()).toContain('WinDomain')

    await wrapper.get('input[type="search"]').setValue('')
    await wrapper.findAll('input[type="checkbox"]')[0].setValue(true)
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.text()).toContain('UnixSSH')
    expect(wrapper.text()).not.toContain('WinDomain')
  })

  it('Save needs an account type and a change, then sends the mapping and fills existing accounts by default', async () => {
    const wrapper = await mountPage()
    const row = () => wrapper.findAll('tbody tr')[0]
    const saveButton = () => row().findAll('button').find(b => b.text() === 'Save')
    expect(saveButton().attributes('disabled')).toBeDefined()

    await pick(row().get('select[aria-label="Account type for UnixSSH"]'), 'Local/OS Account')
    await pick(row().get('select[aria-label="Source of record for UnixSSH"]'), 'Active Directory')
    await row().get('input[aria-label="Notes for UnixSSH"]').setValue('Linux over SSH')
    expect(saveButton().attributes('disabled')).toBeUndefined()
    await saveButton().trigger('click')
    await flushPromises()

    const put = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'PUT')
    expect(put[0]).toBe('/api/admin/platform-mapping/11')
    expect(JSON.parse(put[1].body)).toEqual({ accountTypeKey: 2, sorKey: 7, notes: 'Linux over SSH', fillExisting: true })
    expect(wrapper.get('[role="status"]').text()).toContain('Saved the mapping of UnixSSH: filled 5 account(s) with no account type and 3 with no source of record.')
  })

  it('does not fill existing accounts when that is unticked', async () => {
    const wrapper = await mountPage()
    await wrapper.findAll('input[type="checkbox"]')[1].setValue(false)
    const row = wrapper.findAll('tbody tr')[0]
    await pick(row.get('select[aria-label="Account type for UnixSSH"]'), 'Local/OS Account')
    await row.findAll('button').find(b => b.text() === 'Save').trigger('click')
    await flushPromises()

    const put = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'PUT')
    expect(JSON.parse(put[1].body)).toMatchObject({ accountTypeKey: 2, sorKey: null, fillExisting: false })
    expect(wrapper.get('[role="status"]').text()).toBe('Saved the mapping of UnixSSH.')
  })

  it('Remove asks first, only for a mapped platform', async () => {
    const wrapper = await mountPage()
    expect(wrapper.findAll('tbody tr')[0].findAll('button').some(b => b.text() === 'Remove')).toBe(false)

    const mappedRow = wrapper.findAll('tbody tr')[1]
    confirmDelete.mockResolvedValueOnce(false)
    await mappedRow.findAll('button').find(b => b.text() === 'Remove').trigger('click')
    await flushPromises()
    expect(globalThis.fetch.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(false)

    await mappedRow.findAll('button').find(b => b.text() === 'Remove').trigger('click')
    await flushPromises()
    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/platform-mapping/12', { method: 'DELETE' })
    expect(wrapper.get('[role="status"]').text()).toContain('Removed the mapping of WinDomain.')
  })

  it('shows why a save was refused', async () => {
    const wrapper = await mountPage(rows, { 'PUT /api/admin/platform-mapping/11': jsonResponse({ error: 'Choose an account type.' }, false, 400) })
    const row = wrapper.findAll('tbody tr')[0]
    await pick(row.get('select[aria-label="Account type for UnixSSH"]'), 'Local/OS Account')
    await row.findAll('button').find(b => b.text() === 'Save').trigger('click')
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toBe('Choose an account type.')
  })

  // UI review W8-7
  it('says why Save is off, and Remove cannot be sent twice', async () => {
    const wrapper = await mountPage()
    const row = wrapper.findAll('tbody tr')[0]
    const save = row.findAll('button').find(b => b.text() === 'Save')
    expect(save.attributes('title')).toBe('Choose an account type to save.')

    const mapped = wrapper.findAll('tbody tr')[1]
    expect(mapped.findAll('button').find(b => b.text() === 'Save').attributes('title')).toBe('Nothing has changed yet.')

    let release
    globalThis.fetch = vi.fn((url, init = {}) => (init.method === 'DELETE'
      ? new Promise(resolve => { release = () => resolve(jsonResponse(null, true, 204)) })
      : Promise.resolve(jsonResponse(url.endsWith('/options') ? options : rows))))
    const remove = mapped.findAll('button').find(b => b.text() === 'Remove')
    await remove.trigger('click')
    await flushPromises()
    expect(remove.attributes('disabled')).toBeDefined()
    await remove.trigger('click')
    expect(globalThis.fetch.mock.calls.filter(([, init]) => init?.method === 'DELETE')).toHaveLength(1)
    release()
    await flushPromises()
  })

  it('says so when there are no platforms', async () => {
    const wrapper = await mountPage([])
    expect(wrapper.text()).toContain('No platforms to show.')
  })
})

// D-293: suggestions fill the drop-downs; nothing is saved until Save.
describe('PlatformMapping.vue suggestions', () => {
  afterEach(() => vi.restoreAllMocks())

  const suggested = [
    { ...rows[0], suggestion: { accountTypeKey: 2, accountTypeName: 'Local/OS Account', sorKey: null, sourceOfRecord: null, basis: 'Keyword', detail: 'unix' } },
    { ...rows[0], platformKey: 13, platformId: 'WinDomain', platformName: 'WinDomain', sourceSystemName: 'Self-Hosted',
      suggestion: { accountTypeKey: 1, accountTypeName: 'Domain Account', sorKey: 7, sourceOfRecord: 'Active Directory', basis: 'OtherSource', detail: 'Privilege Cloud' } },
    rows[1]
  ]

  it('says what is suggested and why, and Use fills only that row without saving', async () => {
    const wrapper = await mountPage(suggested)

    expect(wrapper.text()).toContain('Suggested: Local/OS Account (the name contains "unix")')
    expect(wrapper.text()).toContain('Suggested: Domain Account / Active Directory (as the same platform in Privilege Cloud)')

    await wrapper.get('button[aria-label="Use the suggestion for WinDomain"]').trigger('click')
    expect(wrapper.get('select[aria-label="Account type for WinDomain"]').element.value).toBe('1')
    expect(wrapper.get('select[aria-label="Source of record for WinDomain"]').element.value).toBe('7')
    expect(wrapper.get('select[aria-label="Account type for UnixSSH"]').element.value).toBe('(not mapped)')
    expect(wrapper.find('button[aria-label="Use the suggestion for WinDomain"]').exists()).toBe(false)
    expect(globalThis.fetch.mock.calls.some(([, init]) => init?.method === 'PUT')).toBe(false)
  })

  it('fills in every suggestion at once, and never one where a type is already chosen', async () => {
    const wrapper = await mountPage(suggested)
    await pick(wrapper.get('select[aria-label="Account type for UnixSSH"]'), 'Domain Account')

    const fillAll = wrapper.findAll('button').find(b => b.text() === 'Fill in 1 suggestion')
    await fillAll.trigger('click')

    expect(wrapper.get('select[aria-label="Account type for UnixSSH"]').element.value).toBe('1')
    expect(wrapper.get('select[aria-label="Account type for WinDomain"]').element.value).toBe('1')
    expect(wrapper.findAll('button').some(b => b.text().startsWith('Fill in'))).toBe(false)
  })

  it('offers nothing when no platform has a suggestion', async () => {
    const wrapper = await mountPage()
    expect(wrapper.text()).not.toContain('Suggested:')
    expect(wrapper.findAll('button').some(b => b.text().startsWith('Fill in'))).toBe(false)
  })
})
