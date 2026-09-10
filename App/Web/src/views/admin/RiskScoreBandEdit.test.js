import { describe, it, expect, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import RiskScoreBandEdit from './RiskScoreBandEdit.vue'

// D-124 Phase 4: Add/Edit moved off RiskScoreBands.vue's own inline form
// onto this routed page -- establishes coverage for the create-vs-edit-by-
// route-param branching, including the save-error-surfacing (overlapping
// range) case RiskScoreBands.test.js's own removed inline-form test used
// to cover.
function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const bands = [
  { riskScoreBandKey: 1, bandName: 'Low', minScore: 0, maxScore: 250, riskOrder: 10, modifiedDate: null }
]

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

describe('RiskScoreBandEdit.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('create mode: shows an empty New Band form with no fetch needed', async () => {
    globalThis.fetch = vi.fn()
    const wrapper = mount(RiskScoreBandEdit, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.text()).toContain('New Band')
    expect(globalThis.fetch).not.toHaveBeenCalled()
  })

  it('edit mode: fetches the full list, finds the matching band by key, and pre-fills the form', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse(bands))
    const wrapper = mount(RiskScoreBandEdit, { props: { riskScoreBandKey: 1 }, global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/risk-score-bands')
    expect(wrapper.text()).toContain('Edit Band')
    expect(wrapper.get('input[required]').element.value).toBe('Low')
  })

  it('edit mode: shows a load error when the matching key is not found in the list', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse(bands))
    const wrapper = mount(RiskScoreBandEdit, { props: { riskScoreBandKey: 999 }, global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('not found')
    expect(wrapper.find('form').exists()).toBe(false)
  })

  it('shows the server-provided message when a save is rejected (e.g. an overlapping range)', async () => {
    globalThis.fetch = vi.fn()
    const wrapper = mount(RiskScoreBandEdit, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    await wrapper.get('input[required]').setValue('Overlap Test')

    globalThis.fetch.mockResolvedValueOnce(jsonResponse({ message: "Range 100-200 overlaps existing band 'Low' (0-250)." }, false, 400))
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.text()).toContain("Range 100-200 overlaps existing band 'Low' (0-250).")
    expect(wrapper.find('form').exists()).toBe(true)
  })

  it('navigates back to the list on a successful save', async () => {
    globalThis.fetch = vi.fn()
    const router = makeRouter()
    const pushSpy = vi.spyOn(router, 'push')
    const wrapper = mount(RiskScoreBandEdit, { global: { plugins: [router] } })
    await flushPromises()

    await wrapper.get('input[required]').setValue('New Band')
    globalThis.fetch.mockResolvedValueOnce(jsonResponse({ riskScoreBandKey: 9 }, true, 201))
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(pushSpy).toHaveBeenCalledWith({ name: 'admin-risk-score-bands' })
  })
})
