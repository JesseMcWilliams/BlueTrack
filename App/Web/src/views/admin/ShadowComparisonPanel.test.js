import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ShadowComparisonPanel from './ShadowComparisonPanel.vue'

// D-271: the shadow comparison panel on Data Sources.
vi.mock('../../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../../composables/useConfirmDialog'

function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const run = (overrides = {}) => ({
  runKey: 5, pullKey: 9, comparedAt: '2026-10-20T11:00:00', evdStagedAt: '2026-10-20T09:05:00', evdUsable: true, evdNote: null,
  missingFromDirect: 3, missingExplained: 1, onlyInDirect: 4, accountFieldDiffs: 1, accountFieldExplained: 0,
  permissionDiffs: 2, logonDomainDiffs: 1, blockingCount: 3, isClean: false, ...overrides
})

const missingSafe = {
  differenceKey: 11, category: 'MissingFromDirect', itemKind: 'Safe', itemKey: '5', itemName: 'PVWAConfig', safeName: 'PVWAConfig',
  field: null, evdValue: null, directValue: null, markCategory: null, blocking: true, safeNumber: 5, principalId: null
}
const missingMember = {
  differenceKey: 12, category: 'MissingFromDirect', itemKind: 'Member', itemKey: '12:17', itemName: 'PVWAGWAccounts', safeName: 'BizSafe',
  field: null, evdValue: null, directValue: null, markCategory: null, blocking: true, safeNumber: 12, principalId: 17
}
const permission = {
  differenceKey: 13, category: 'Permission', itemKind: 'Member', itemKey: '12:20', itemName: 'someone', safeName: 'BizSafe',
  field: 'ManageSafe', evdValue: 'YES', directValue: 'NO', markCategory: null, blocking: false, safeNumber: 12, principalId: 20
}

function comparison(overrides = {}) {
  return {
    cleanNightsNeeded: 7, cleanNights: 2, runs: [run(), run({ runKey: 4, isClean: true, blockingCount: 0 })],
    differences: [missingSafe, missingMember, permission], totalDifferences: 3,
    marks: [{ markKey: 1, itemKind: 'Group', itemKey: '30', itemName: 'Gateway', category: 'Expected', note: 'checked', markedByName: 'Admin', markedAt: '2026-10-19T10:00:00' }],
    ...overrides
  }
}

describe('ShadowComparisonPanel.vue', () => {
  beforeEach(() => {
    confirmDelete.mockResolvedValue(true)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows the clean nights, the latest counts, the differences and the marks', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse(comparison())))
    const wrapper = mount(ShadowComparisonPanel)
    await flushPromises()

    const text = wrapper.text()
    expect(text).toContain('2 of 7 clean nights in a row.')
    expect(text).toContain('Not clean: 3 unexplained difference(s)')
    expect(text).toContain('3 (1 marked as expected)')
    expect(text).toContain('PVWAConfig')
    expect(text).toContain('Permission differs')
    expect(text).toContain('Gateway')
    // Only differences that can stop a clean night offer Mark.
    expect(wrapper.findAll('button').filter(b => b.text() === 'Mark as expected')).toHaveLength(2)
  })

  it('says when the switch can be made', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse(comparison({ cleanNights: 7, runs: [run({ isClean: true, blockingCount: 0 })] }))))
    const wrapper = mount(ShadowComparisonPanel)
    await flushPromises()

    expect(wrapper.text()).toContain('Ready to switch the Self-Hosted source to Direct')
  })

  it('explains a night whose EVD data was not usable', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse(comparison({
      runs: [run({ evdUsable: false, evdNote: 'The staged Self-Hosted data came from a Direct pull, not the EVD.' })]
    }))))
    const wrapper = mount(ShadowComparisonPanel)
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('came from a Direct pull')
  })

  it('filters by category and blocking only', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse(comparison())))
    const wrapper = mount(ShadowComparisonPanel)
    await flushPromises()

    await wrapper.find('select').setValue('Permission')
    await flushPromises()
    expect(globalThis.fetch).toHaveBeenLastCalledWith('/api/admin/cyberark-sources/direct/comparison?category=Permission')

    await wrapper.find('input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(globalThis.fetch).toHaveBeenLastCalledWith('/api/admin/cyberark-sources/direct/comparison?category=Permission&blockingOnly=true')
  })

  it('marks every membership of a member\'s group, then reloads', async () => {
    globalThis.fetch = vi.fn((url, options) => Promise.resolve(options?.method === 'POST' ? { ok: true, status: 204, json: () => Promise.resolve(null) } : jsonResponse(comparison())))
    const wrapper = mount(ShadowComparisonPanel)
    await flushPromises()

    await wrapper.find('button[aria-label="Mark PVWAGWAccounts as expected"]').trigger('click')
    expect(wrapper.text()).toContain('Every Safe membership of PVWAGWAccounts')
    await wrapper.find('input[type="radio"][value="Principal"]').setValue(true)
    await wrapper.find('.mark-form select').setValue('Expected')
    await wrapper.find('.mark-form input[maxlength="500"]').setValue('PVWA gateway group')
    await wrapper.find('.mark-form').trigger('submit')
    await flushPromises()

    const post = globalThis.fetch.mock.calls.find(([, options]) => options?.method === 'POST')
    expect(post[0]).toBe('/api/admin/cyberark-sources/direct/comparison/marks')
    expect(JSON.parse(post[1].body)).toEqual({ differenceKey: 12, scope: 'Principal', category: 'Expected', note: 'PVWA gateway group' })
    expect(wrapper.text()).toContain('Marked. The latest comparison has been updated.')
    expect(wrapper.find('.mark-form').exists()).toBe(false)
  })

  it('offers the whole Safe for a Safe difference but not a principal', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse(comparison())))
    const wrapper = mount(ShadowComparisonPanel)
    await flushPromises()

    await wrapper.find('button[aria-label="Mark PVWAConfig as expected"]').trigger('click')

    expect(wrapper.find('input[type="radio"][value="Safe"]').exists()).toBe(true)
    expect(wrapper.find('input[type="radio"][value="Principal"]').exists()).toBe(false)
  })

  it('shows why a mark was refused', async () => {
    globalThis.fetch = vi.fn((url, options) => Promise.resolve(options?.method === 'POST'
      ? jsonResponse({ title: 'That difference is no longer in the latest comparison' }, false, 404)
      : jsonResponse(comparison())))
    const wrapper = mount(ShadowComparisonPanel)
    await flushPromises()

    await wrapper.find('button[aria-label="Mark PVWAConfig as expected"]').trigger('click')
    await wrapper.find('.mark-form').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toBe('That difference is no longer in the latest comparison')
  })

  it('removes a mark after asking', async () => {
    globalThis.fetch = vi.fn((url, options) => Promise.resolve(options?.method === 'DELETE' ? { ok: true, status: 204 } : jsonResponse(comparison())))
    const wrapper = mount(ShadowComparisonPanel)
    await flushPromises()

    await wrapper.find('button[aria-label="Remove the mark on Gateway"]').trigger('click')
    await flushPromises()

    expect(confirmDelete).toHaveBeenCalled()
    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/cyberark-sources/direct/comparison/marks/1', { method: 'DELETE' })
    expect(wrapper.text()).toContain('Mark removed.')
  })

  it('lists the Safes PVWA can\'t fully see, and marks a whole Safe from there', async () => {
    const safes = [
      { safeNumber: 5, safeName: 'PVWAConfig', notReturned: true, accountsMissing: 1, membersMissing: 9, blocking: true, markCategory: null, anchorDifferenceKey: 11 },
      { safeNumber: 12, safeName: 'BizSafe', notReturned: false, accountsMissing: 0, membersMissing: 1, blocking: false, markCategory: null, anchorDifferenceKey: 12 }
    ]
    globalThis.fetch = vi.fn((url, options) => Promise.resolve(options?.method === 'POST' ? { ok: true, status: 204, json: () => Promise.resolve(null) } : jsonResponse(comparison({ safes }))))
    const wrapper = mount(ShadowComparisonPanel)
    await flushPromises()

    const rows = wrapper.findAll('table.safes tbody tr').map(r => r.text())
    expect(rows[0]).toContain('PVWAConfig')
    expect(rows[0]).toContain('No')
    expect(rows[1]).toContain('Yes, partly')
    expect(rows[1]).toContain('Explained')   // its member difference is covered by a mark

    await wrapper.find('button[aria-label="Mark Safe PVWAConfig as expected"]').trigger('click')
    expect(wrapper.find('input[type="radio"][value="Item"]').exists()).toBe(false)
    await wrapper.find('.mark-form select').setValue('Internal')
    await wrapper.find('.mark-form').trigger('submit')
    await flushPromises()

    const post = globalThis.fetch.mock.calls.find(([, options]) => options?.method === 'POST')
    expect(JSON.parse(post[1].body)).toEqual({ differenceKey: 11, scope: 'Safe', category: 'Internal', note: null })
  })

  it('says which marks the comparison made by itself', async () => {
    const autoSafe = { ...missingSafe, markCategory: 'Internal', markAuto: true, blocking: false }
    const autoMember = { ...missingMember, markCategory: 'Expected', markAuto: true, blocking: false }
    const safes = [{ safeNumber: 5, safeName: 'PVWAConfig', notReturned: true, accountsMissing: 0, membersMissing: 9, blocking: false, markCategory: 'Internal', markAuto: true, anchorDifferenceKey: 11 }]
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse(comparison({ differences: [autoSafe, autoMember], safes }))))
    const wrapper = mount(ShadowComparisonPanel)
    await flushPromises()

    expect(wrapper.find('table.safes tbody tr').text()).toContain('Internal (automatic)')
    expect(wrapper.text()).toContain('Expected (automatic)')
    expect(wrapper.findAll('button').filter(b => b.text() === 'Mark as expected')).toHaveLength(0)
  })

  it('says when PVWA saw every Safe', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse(comparison({ safes: [] }))))
    const wrapper = mount(ShadowComparisonPanel)
    await flushPromises()

    expect(wrapper.text()).toContain('None: PVWA returned every Safe the EVD has')
  })

  it('reloads when the page says a shadow pull finished', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse(comparison())))
    const wrapper = mount(ShadowComparisonPanel, { props: { refreshKey: 0 } })
    await flushPromises()
    const calls = globalThis.fetch.mock.calls.length

    await wrapper.setProps({ refreshKey: 1 })
    await flushPromises()

    expect(globalThis.fetch.mock.calls.length).toBe(calls + 1)
  })
})
