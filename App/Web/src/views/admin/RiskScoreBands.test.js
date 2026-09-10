import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import RiskScoreBands from './RiskScoreBands.vue'

// D-128: Delete now awaits the shared confirmDelete(...) dialog
// (App/Web/src/components/ConfirmDialog.vue, mounted once in App.vue --
// not present in this isolated component mount) before proceeding. Mocked
// here so this file's own tests stay focused on RiskScoreBands.vue's
// delete-wiring, not the shared dialog's own UI (which has no dedicated
// test file yet, matching this app's current coverage for other new
// shared components like Pager.vue).
vi.mock('../../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../../composables/useConfirmDialog'

// D-120: this project had no existing Vitest component coverage for
// Targets.vue/AccessGroups.vue to mirror (only src/stores/*.test.js exists
// today) -- this establishes the pattern for the new page instead, using
// the same globalThis.fetch mocking style rights.test.js/theme.test.js
// already use for their own async calls.
function jsonResponse(body, ok = true, status = 200) {
  return {
    ok,
    status,
    json: () => Promise.resolve(body)
  }
}

// D-124 Phase 4: RiskScoreBands.vue now links "+ New Band"/each row's
// "Edit" to RiskScoreBandEdit.vue's routes instead of toggling an inline
// form -- needs a real router installed so <router-link> resolves.
function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/admin/risk-score-bands', name: 'admin-risk-score-bands', component: { template: '<div />' } },
      { path: '/admin/risk-score-bands/new', name: 'admin-risk-score-band-create', component: { template: '<div />' } },
      { path: '/admin/risk-score-bands/:riskScoreBandKey', name: 'admin-risk-score-band-edit', component: { template: '<div />' } }
    ]
  })
}

describe('RiskScoreBands.vue', () => {
  beforeEach(() => {
    globalThis.fetch = vi.fn()
    // vi.restoreAllMocks() below clears confirmDelete's mocked resolved
    // value (set once, at module-mock time) back to a bare vi.fn() after
    // the first test that uses it -- re-establish it fresh before every test.
    confirmDelete.mockResolvedValue(true)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads and renders the seeded bands', async () => {
    globalThis.fetch.mockResolvedValueOnce(jsonResponse([
      { riskScoreBandKey: 1, bandName: 'Low', minScore: 0, maxScore: 250, riskOrder: 10, modifiedDate: null },
      { riskScoreBandKey: 2, bandName: 'Medium', minScore: 251, maxScore: 500, riskOrder: 20, modifiedDate: null }
    ]))

    const wrapper = mount(RiskScoreBands, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Low')
    expect(wrapper.text()).toContain('Medium')
    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/risk-score-bands')
  })

  it('shows an error message when the initial load fails', async () => {
    globalThis.fetch.mockResolvedValueOnce(jsonResponse(null, false, 500))

    const wrapper = mount(RiskScoreBands, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('500')
  })

  // D-124 Phase 4: Add/Edit moved off this page's own inline form onto
  // RiskScoreBandEdit.vue's routed pages -- the save-error-surfacing
  // coverage (including the overlapping-range case) moved to
  // RiskScoreBandEdit.test.js.
  // D-125: "+ New Band" is now a real <button> (this app's button styling
  // is scoped to the button element itself, not a reusable class),
  // navigating imperatively via router.push -- asserts on the router's
  // resulting location instead of an href.
  it('"+ New Band" button navigates to the admin-risk-score-band-create route', async () => {
    globalThis.fetch.mockResolvedValueOnce(jsonResponse([]))

    const router = makeRouter()
    const wrapper = mount(RiskScoreBands, { global: { plugins: [router] } })
    await flushPromises()

    const button = wrapper.findAll('button').find(b => b.text() === '+ New Band')
    await button.trigger('click')
    await flushPromises()

    expect(router.currentRoute.value.name).toBe('admin-risk-score-band-create')
  })

  it('links each row\'s "Edit" to the admin-risk-score-band-edit route with that row\'s key', async () => {
    globalThis.fetch.mockResolvedValueOnce(jsonResponse([
      { riskScoreBandKey: 1, bandName: 'Low', minScore: 0, maxScore: 250, riskOrder: 10, modifiedDate: null }
    ]))
    const wrapper = mount(RiskScoreBands, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const link = wrapper.findAll('a').find(a => a.text() === 'Edit')
    expect(link.attributes('href')).toBe('/admin/risk-score-bands/1')
  })

  it('reloads the list after a successful delete', async () => {
    globalThis.fetch.mockResolvedValueOnce(jsonResponse([
      { riskScoreBandKey: 1, bandName: 'Low', minScore: 0, maxScore: 250, riskOrder: 10, modifiedDate: null }
    ]))
    const wrapper = mount(RiskScoreBands, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    globalThis.fetch.mockResolvedValueOnce(jsonResponse(null, true, 204))
    globalThis.fetch.mockResolvedValueOnce(jsonResponse([]))

    const deleteButton = wrapper.findAll('button').find(b => b.text() === 'Delete')
    await deleteButton.trigger('click')
    await flushPromises()

    expect(confirmDelete).toHaveBeenCalledWith(expect.stringContaining('Low'))
    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/risk-score-bands/1', { method: 'DELETE' })
  })

  // D-128: confirmDelete resolving false (Cancel/Escape in the shared
  // dialog) must stop the delete before any DELETE request is sent.
  it('does not delete when the confirmation is declined', async () => {
    globalThis.fetch.mockResolvedValueOnce(jsonResponse([
      { riskScoreBandKey: 1, bandName: 'Low', minScore: 0, maxScore: 250, riskOrder: 10, modifiedDate: null }
    ]))
    const wrapper = mount(RiskScoreBands, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    confirmDelete.mockResolvedValueOnce(false)
    const fetchCallsBefore = globalThis.fetch.mock.calls.length

    const deleteButton = wrapper.findAll('button').find(b => b.text() === 'Delete')
    await deleteButton.trigger('click')
    await flushPromises()

    expect(globalThis.fetch.mock.calls.length).toBe(fetchCallsBefore) // no new (DELETE) call was made
  })
})
