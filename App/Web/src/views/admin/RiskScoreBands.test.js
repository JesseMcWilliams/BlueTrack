import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import RiskScoreBands from './RiskScoreBands.vue'

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

describe('RiskScoreBands.vue', () => {
  beforeEach(() => {
    globalThis.fetch = vi.fn()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads and renders the seeded bands', async () => {
    globalThis.fetch.mockResolvedValueOnce(jsonResponse([
      { riskScoreBandKey: 1, bandName: 'Low', minScore: 0, maxScore: 250, riskOrder: 10, modifiedDate: null },
      { riskScoreBandKey: 2, bandName: 'Medium', minScore: 251, maxScore: 500, riskOrder: 20, modifiedDate: null }
    ]))

    const wrapper = mount(RiskScoreBands)
    await flushPromises()

    expect(wrapper.text()).toContain('Low')
    expect(wrapper.text()).toContain('Medium')
    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/risk-score-bands')
  })

  it('shows an error message when the initial load fails', async () => {
    globalThis.fetch.mockResolvedValueOnce(jsonResponse(null, false, 500))

    const wrapper = mount(RiskScoreBands)
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('500')
  })

  it('opens the New Band form with defaults on + New Band', async () => {
    globalThis.fetch.mockResolvedValueOnce(jsonResponse([]))

    const wrapper = mount(RiskScoreBands)
    await flushPromises()

    await wrapper.get('button.btn-primary').trigger('click')

    expect(wrapper.find('form').exists()).toBe(true)
    expect(wrapper.text()).toContain('New Band')
  })

  it('shows the server-provided message when a save is rejected (e.g. an overlapping range)', async () => {
    globalThis.fetch.mockResolvedValueOnce(jsonResponse([]))
    const wrapper = mount(RiskScoreBands)
    await flushPromises()

    await wrapper.get('button.btn-primary').trigger('click')
    await wrapper.get('input[required]').setValue('Overlap Test')

    globalThis.fetch.mockResolvedValueOnce(jsonResponse({ message: "Range 100-200 overlaps existing band 'Low' (0-250)." }, false, 400))

    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.text()).toContain("Range 100-200 overlaps existing band 'Low' (0-250).")
  })

  it('reloads the list after a successful delete', async () => {
    globalThis.fetch.mockResolvedValueOnce(jsonResponse([
      { riskScoreBandKey: 1, bandName: 'Low', minScore: 0, maxScore: 250, riskOrder: 10, modifiedDate: null }
    ]))
    const wrapper = mount(RiskScoreBands)
    await flushPromises()

    globalThis.fetch.mockResolvedValueOnce(jsonResponse(null, true, 204))
    globalThis.fetch.mockResolvedValueOnce(jsonResponse([]))

    const deleteButton = wrapper.findAll('button').find(b => b.text() === 'Delete')
    await deleteButton.trigger('click')
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/risk-score-bands/1', { method: 'DELETE' })
  })
})
