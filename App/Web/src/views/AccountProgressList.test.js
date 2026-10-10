import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import AccountProgressList from './AccountProgressList.vue'

// D-124 Phase 3: this page had no component-level Vitest coverage before
// this phase (it predates Targets.test.js/AccessGroups.test.js's own
// D-121 precedent, the freshest one at the time this file was written) --
// this establishes coverage for the new pager UI and the two-count summary
// (FilterCountSummary + useTotalCount's X-Filtered-Count/X-Total-Count),
// following the same globalThis.fetch mocking style.
function jsonResponse(body, { ok = true, status = 200, totalCount = null, filteredCount = totalCount } = {}) {
  return {
    ok,
    status,
    headers: {
      get: (name) => {
        if (name === 'X-Total-Count') return totalCount !== null ? String(totalCount) : null
        if (name === 'X-Filtered-Count') return filteredCount !== null ? String(filteredCount) : null
        return null
      }
    },
    json: () => Promise.resolve(body)
  }
}

const sampleAccount = {
  accountKey: 1,
  accountName: 'svc-web01',
  userName: 'svc-web01-user',
  address: 'web01.example.com',
  stageName: 'Discovery',
  statusName: 'In Progress',
  riskLevelName: 'Medium',
  ownerName: 'Jane Analyst',
  targetRemediationDate: null,
  actualCompletionDate: null,
  effectiveRiskScore: 400,
  riskScoreBandName: 'Medium'
}

// Mounting triggers: GET /api/account-progress/reference-data, then load()'s GET /api/account-progress.
function mockInitialLoad({ accounts = [sampleAccount], totalCount = accounts.length, filteredCount = totalCount } = {}) {
  globalThis.fetch = vi.fn((url) => {
    if (url === '/api/account-progress/reference-data') return Promise.resolve(jsonResponse({}))
    if (url.startsWith('/api/account-progress')) return Promise.resolve(jsonResponse(accounts, { totalCount, filteredCount }))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

// router-link is used by this page (account-progress-detail route) -- a
// minimal real router avoids needing to stub it component-by-component.
function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/account-progress/:accountKey', name: 'account-progress-detail', component: { template: '<div />' } }
    ]
  })
}

describe('AccountProgressList.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  // D-133: the Username/Address columns replaced the old single Account
  // (AccountName) column -- Username is now the clickable link to the
  // detail page (the same "click the name to edit" precedent, D-125).
  it('loads and renders the account list, showing Username and Address instead of Account Name', async () => {
    mockInitialLoad()
    const wrapper = mount(AccountProgressList, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('svc-web01-user')
    expect(wrapper.text()).toContain('web01.example.com')
  })

  it('shows the "Showing N of M matching (of total)" count summary once both headers are known', async () => {
    mockInitialLoad({ accounts: [sampleAccount], totalCount: 500, filteredCount: 30 })
    const wrapper = mount(AccountProgressList, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Showing 1–1 of 30 matching (500 total)')
  })

  it('sends page/pageSize query params on load, defaulting pageSize to 50', async () => {
    mockInitialLoad({ accounts: [] })
    const wrapper = mount(AccountProgressList, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/account-progress?'))
    expect(calls.some(u => u.includes('page=1') && u.includes('pageSize=50'))).toBe(true)
  })

  it('clicking Next advances to page 2, and a subsequent filter change resets back to page 1', async () => {
    mockInitialLoad({ accounts: [sampleAccount], totalCount: 200, filteredCount: 200 })
    const wrapper = mount(AccountProgressList, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const nextButton = wrapper.findAll('.pager button').find(b => b.text().includes('Next'))
    await nextButton.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Page 2 of')

    const ownerInput = wrapper.get('input[type="text"]')
    await ownerInput.setValue('jane')
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/account-progress?'))
    expect(calls.at(-1)).toContain('page=1')
    expect(calls.at(-1)).toContain('owner=jane')
  })

  // D-178: one search box for Username or Address, sent as `search`.
  it('the search box sends a trimmed search param and resets to page 1; blank sends none', async () => {
    mockInitialLoad({ accounts: [sampleAccount], totalCount: 200, filteredCount: 200 })
    const wrapper = mount(AccountProgressList, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const nextButton = wrapper.findAll('.pager button').find(b => b.text().includes('Next'))
    await nextButton.trigger('click')
    await flushPromises()

    const searchInput = wrapper.get('input[type="search"]')
    await searchInput.setValue('  web01  ')
    await flushPromises()
    let calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/account-progress?'))
    expect(calls.at(-1)).toContain('search=web01&')
    expect(calls.at(-1)).toContain('page=1')

    await searchInput.setValue('   ')
    await flushPromises()
    calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/account-progress?'))
    expect(calls.at(-1)).not.toContain('search=')
  })

  it('changing the page size persists the preference and re-fetches with the new pageSize', async () => {
    mockInitialLoad({ accounts: [sampleAccount], totalCount: 200, filteredCount: 200 })
    const wrapper = mount(AccountProgressList, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const pageSizeSelect = wrapper.find('.pager select')
    await pageSizeSelect.setValue('100')
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/me/preferences/PageSize', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ value: '100' })
    })
    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/account-progress?'))
    expect(calls.at(-1)).toContain('pageSize=100')
  })
  // D-185: deleted accounts are hidden unless the Deleted filter shows them,
  // and a deleted row is marked with who deleted it and why.
  it('sends deleted=Only when "Only deleted" is chosen, and marks deleted rows', async () => {
    mockInitialLoad({
      accounts: [{ ...sampleAccount, isDeleted: true, isDeletedInSource: false, deletedByName: 'Pat Admin', deletionReason: 'Decommissioned server' }]
    })
    const wrapper = mount(AccountProgressList, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const badge = wrapper.find('.deleted-badge')
    expect(badge.exists()).toBe(true)
    expect(badge.attributes('title')).toBe('Deleted in BlueTrack by Pat Admin: Decommissioned server')

    const deletedSelect = wrapper.findAll('select').find(sel => sel.findAll('option').some(o => o.text() === 'Only deleted'))
    await deletedSelect.setValue('Only')
    await flushPromises()
    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/account-progress?'))
    expect(calls.at(-1)).toContain('deleted=Only')
  })
  it('labels an account dropped by the ignored-safe pattern as not imported', async () => {
    mockInitialLoad({ accounts: [{ ...sampleAccount, isDeleted: true, isDeletedInSource: true, isInIgnoredSafe: true }] })
    const wrapper = mount(AccountProgressList, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.find('.deleted-badge').attributes('title')).toBe('Not imported (ignored safe)')
  })
  // D-191: an account without a username is still a link, by its account name.
  it('links an account without a username by its account name, marked (no username)', async () => {
    mockInitialLoad({ accounts: [{ ...sampleAccount, userName: null, address: null, accountName: 'salesforce_hash_key' }] })
    const wrapper = mount(AccountProgressList, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const link = wrapper.findAll('a').find(a => a.text() === 'salesforce_hash_key')
    expect(link.attributes('href')).toBe('/account-progress/1')
    expect(wrapper.find('.no-username').text()).toBe('(no username)')
  })
})
