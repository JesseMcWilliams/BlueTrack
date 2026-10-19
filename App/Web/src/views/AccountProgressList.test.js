import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import AccountProgressList from './AccountProgressList.vue'
import { useListStateStore } from '../stores/listState'

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
      { path: '/accounts', name: 'account-progress-list', component: { template: '<div />' } },
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

  // UI review W2-7
  it('offers Clear filters only while a filter is set, and clearing reloads once with the defaults', async () => {
    mockInitialLoad({ accounts: [sampleAccount], totalCount: 5, filteredCount: 1 })
    const wrapper = mount(AccountProgressList, { global: { plugins: [makeRouter()] } })
    await flushPromises()
    const clear = () => wrapper.findAll('button').find(b => b.text() === 'Clear filters')
    expect(clear()).toBeUndefined()

    await wrapper.find('input[type="search"]').setValue('web01')
    await wrapper.findAll('select').find(s => s.text().includes('Only deleted')).setValue('Show')
    await flushPromises()
    expect(clear()).toBeDefined()
    globalThis.fetch.mockClear()

    await clear().trigger('click')
    await flushPromises()

    expect(wrapper.find('input[type="search"]').element.value).toBe('')
    expect(clear()).toBeUndefined()
    const loads = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/account-progress?'))
    expect(loads).toHaveLength(1)
    expect(loads[0]).not.toContain('search=')
    expect(loads[0]).not.toContain('deleted=')
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
  // Replies can arrive out of order: a slow reply to the search must not overwrite the newer filter's rows
  // (found in E2E: "Deleted accounts: Only" straight after typing a search sometimes showed the search's rows).
  it('drops a slow reply to an earlier request that arrives after a newer one', async () => {
    const searchRows = [{ ...sampleAccount, accountKey: 11, userName: 'from-the-search' }]
    const deletedRows = [{ ...sampleAccount, accountKey: 12, userName: 'from-the-deleted-filter' }]
    let releaseSearch
    globalThis.fetch = vi.fn((url) => {
      if (url === '/api/account-progress/reference-data') return Promise.resolve(jsonResponse({}))
      if (url.includes('deleted=Only')) return Promise.resolve(jsonResponse(deletedRows, { totalCount: 1 }))   // the newer request (it carries the search too)
      if (url.includes('search=web')) return new Promise(resolve => { releaseSearch = () => resolve(jsonResponse(searchRows, { totalCount: 1 })) })
      return Promise.resolve(jsonResponse([sampleAccount], { totalCount: 1 }))
    })
    const wrapper = mount(AccountProgressList, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    await wrapper.get('input[type="search"]').setValue('web')   // slow: not answered yet
    await wrapper.findAll('select').find(select => select.findAll('option').some(o => o.text() === 'Only deleted')).setValue('Only')
    await flushPromises()
    expect(wrapper.text()).toContain('from-the-deleted-filter')

    releaseSearch()   // the older reply arrives last
    await flushPromises()
    expect(wrapper.text()).toContain('from-the-deleted-filter')
    expect(wrapper.text()).not.toContain('from-the-search')
    expect(wrapper.text()).not.toContain('Loading')
  })

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
  // D-212: a feed account shows its source; a linked one (shown on request) links to its CyberArk account.
  it('marks feed accounts with their source, and sends linked=Show when linked feed accounts are shown', async () => {
    mockInitialLoad({
      accounts: [
        { ...sampleAccount, sourceSystemCode: 'PRIVCLOUD', sourceSystemName: 'CyberArk Privilege Cloud (SaaS)' },
        { ...sampleAccount, accountKey: 2, sourceSystemCode: 'CMDB', sourceSystemName: 'CMDB', linkedToAccountKey: 1, linkedToAccountName: 'svc-web01',
          isDeleted: true, isDeletedInSource: true }
      ]
    })
    const wrapper = mount(AccountProgressList, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const badges = wrapper.findAll('.source-badge')
    expect(badges).toHaveLength(1)
    expect(badges[0].text()).toBe('from CMDB')
    expect(wrapper.findAll('.deleted-badge').at(-1).attributes('title')).toBe('Deleted in CMDB')
    const linked = wrapper.find('.linked-badge')
    expect(linked.attributes('href')).toBe('/account-progress/1')
    expect(linked.attributes('title')).toBe('Linked to CyberArk account svc-web01')

    const linkedSelect = wrapper.findAll('select').find(sel => sel.findAll('option').some(o => o.text() === 'Show') && !sel.findAll('option').some(o => o.text() === 'Only deleted'))
    await linkedSelect.setValue('Show')
    await flushPromises()
    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/account-progress?'))
    expect(calls.at(-1)).toContain('linked=Show')
  })

  // UI review W2-2: the filters, sort and page are in the page address.
  describe('list state in the address', () => {
    const referenceData = {
      dim_blueprint_stage: [{ key: 1, name: 'Discovery' }, { key: 2, name: 'Planning' }],
      dim_progress_status: [{ key: 1, name: 'In Progress' }]
    }

    function mockWithReferenceData(totalCount = 200) {
      globalThis.fetch = vi.fn((url) => {
        if (url === '/api/account-progress/reference-data') return Promise.resolve(jsonResponse(referenceData))
        if (url.startsWith('/api/account-progress')) return Promise.resolve(jsonResponse([sampleAccount], { totalCount, filteredCount: totalCount }))
        return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
      })
    }

    async function mountAt(path) {
      const router = makeRouter()
      await router.push(path)
      const wrapper = mount(AccountProgressList, { global: { plugins: [router] } })
      await flushPromises()
      return { wrapper, router }
    }

    const listCalls = () => globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/account-progress?'))
    const lastListCall = () => new URLSearchParams(listCalls().at(-1).split('?')[1])

    it('reads the filters, sort and page from the address into the first request and the controls', async () => {
      mockWithReferenceData()
      const { wrapper } = await mountAt('/accounts?stage=Discovery&status=In%20Progress&search=web&owner=jane&deleted=Only&linked=Show&sort=userName:desc,address:asc&page=2')

      const first = new URLSearchParams(listCalls()[0].split('?')[1])
      expect(Object.fromEntries(first)).toMatchObject({
        stage: 'Discovery', status: 'In Progress', search: 'web', owner: 'jane', deleted: 'Only', linked: 'Show',
        sort: 'userName:desc,address:asc', page: '2'
      })
      expect(wrapper.find('input[type="search"]').element.value).toBe('web')
      expect(wrapper.text()).toContain('Page 2 of')
      expect(wrapper.findAll('th').find(th => th.text().startsWith('Username')).attributes('aria-sort')).toBe('descending')
    })

    it('an untouched list has a clean address and an unfiltered first request', async () => {
      mockWithReferenceData()
      const { router } = await mountAt('/accounts')

      const params = lastListCall()
      expect(params.get('stage')).toBeNull()
      expect(params.get('deleted')).toBeNull()
      expect(params.get('page')).toBe('1')
      expect(router.currentRoute.value.query).toEqual({})
    })

    it('ignores a value the page does not have: an unknown deleted option, an unknown sort field, a bad page', async () => {
      mockWithReferenceData()
      await mountAt('/accounts?deleted=Everything&sort=bogus:asc&page=-4')

      const params = lastListCall()
      expect(params.get('deleted')).toBeNull()
      expect(params.get('sort')).toBeNull()
      expect(params.get('page')).toBe('1')
    })

    it('writes a change to the address, and takes it out again when cleared', async () => {
      mockWithReferenceData()
      const { wrapper, router } = await mountAt('/accounts')

      await wrapper.find('input[type="search"]').setValue('svc')
      await flushPromises()
      expect(router.currentRoute.value.query).toEqual({ search: 'svc' })

      await wrapper.find('input[type="search"]').setValue('')
      await flushPromises()
      expect(router.currentRoute.value.query).toEqual({})
    })

    it('replaces the address entry rather than adding one, so Back leaves the list', async () => {
      mockWithReferenceData()
      const router = makeRouter()
      await router.push('/') // the first navigation of a router replaces, so give Back somewhere to go
      await router.push('/accounts')
      const wrapper = mount(AccountProgressList, { global: { plugins: [router] } })
      await flushPromises()

      await wrapper.find('input[type="search"]').setValue('svc')
      await flushPromises()
      await wrapper.find('input[type="search"]').setValue('svcx')
      await flushPromises()
      router.back()
      await flushPromises()

      expect(router.currentRoute.value.name).toBe('home') // one Back from the list, not one per keystroke
    })

    it('a sort click is in the address; the page goes in when it is not the first, and a filter change drops it', async () => {
      mockWithReferenceData()
      const { wrapper, router } = await mountAt('/accounts')

      await wrapper.findAll('th').find(th => th.text().startsWith('Address')).find('button').trigger('click')
      await flushPromises()
      expect(router.currentRoute.value.query).toEqual({ sort: 'address:asc' })

      await wrapper.findAll('.pager button').find(b => b.text().includes('Next')).trigger('click')
      await flushPromises()
      expect(router.currentRoute.value.query).toEqual({ sort: 'address:asc', page: '2' })

      await wrapper.find('input[type="search"]').setValue('svc')
      await flushPromises()
      expect(router.currentRoute.value.query).toEqual({ sort: 'address:asc', search: 'svc' })   // back on page 1
    })

    it('a stage chosen in the dropdown is in the address', async () => {
      mockWithReferenceData()
      const { wrapper, router } = await mountAt('/accounts')

      await wrapper.findAll('select')[0].setValue('Planning')
      await flushPromises()

      expect(router.currentRoute.value.query).toEqual({ stage: 'Planning' })
      expect(lastListCall().get('stage')).toBe('Planning')
    })

    it('remembers the view for the places that lead back to the list', async () => {
      mockWithReferenceData()
      const { wrapper } = await mountAt('/accounts?stage=Discovery')
      const store = useListStateStore()
      expect(store.queryFor('account-progress-list')).toEqual({ stage: 'Discovery' })

      await wrapper.find('input[type="search"]').setValue('svc')
      await flushPromises()

      expect(store.queryFor('account-progress-list')).toEqual({ stage: 'Discovery', search: 'svc' })
    })

    it('follows the address when it changes under the page (Back, Forward, a link), keeping the page it names', async () => {
      mockWithReferenceData()
      const { wrapper, router } = await mountAt('/accounts?stage=Discovery')
      const calls = listCalls().length

      await router.push('/accounts?stage=Planning&page=3')
      await flushPromises()

      expect(listCalls().length).toBe(calls + 1)   // one reload, not one per field
      const params = lastListCall()
      expect(params.get('stage')).toBe('Planning')
      expect(params.get('page')).toBe('3')
      expect(wrapper.findAll('select')[0].element.value).toBe('Planning')
      expect(router.currentRoute.value.query).toEqual({ stage: 'Planning', page: '3' })
    })

    it('does not reload when the address is already what the page shows', async () => {
      mockWithReferenceData()
      const { wrapper, router } = await mountAt('/accounts')
      await wrapper.find('input[type="search"]').setValue('svc')
      await flushPromises()
      const calls = listCalls().length

      await router.replace({ query: { search: 'svc' } })
      await flushPromises()

      expect(listCalls().length).toBe(calls)
    })

    it('leaving the list does not reload it', async () => {
      mockWithReferenceData()
      const { router } = await mountAt('/accounts?stage=Discovery')
      const calls = listCalls().length

      await router.push('/account-progress/1')
      await flushPromises()

      expect(listCalls().length).toBe(calls)
    })
  })
})
