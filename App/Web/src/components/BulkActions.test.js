import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import SelectionBar from './SelectionBar.vue'
import BulkFieldEditor from './BulkFieldEditor.vue'
import { useListSelectionStore } from '../stores/listSelection'
import { useBulkAction } from '../composables/useBulkAction'

// D-190: the shared bulk-action pieces used by Targets, Access Groups,
// Risk Exceptions and Account Progress.
function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

describe('shared bulk actions', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('keeps a separate selection per list', () => {
    const targets = useListSelectionStore('targets')
    const groups = useListSelectionStore('accessGroups')
    targets.add([1, 2])
    expect(groups.count).toBe(0)
    expect(useListSelectionStore('targets').count).toBe(2)
  })

  it('SelectionBar selects all matching keys, or says when too many match', async () => {
    const selection = useListSelectionStore('targets')
    selection.setEnabled(true)
    globalThis.fetch = vi.fn()
      .mockResolvedValueOnce(jsonResponse({ matchingCount: 3, maxItems: 500, keys: [4, 5, 6] }))
      .mockResolvedValueOnce(jsonResponse({ matchingCount: 900, maxItems: 500, keys: [] }))
    const wrapper = mount(SelectionBar, { props: { selection, pageKeys: [4], filteredCount: 3, keysUrl: '/api/admin/targets/keys?' } })

    const selectAll = () => wrapper.findAll('button').find(b => b.text().startsWith('Select all matching')).trigger('click')
    await selectAll()
    await flushPromises()
    expect(selection.keys).toEqual([4, 5, 6])
    expect(wrapper.text()).toContain('3 selected (1 on this page)')

    await selectAll()
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('900 match, more than the bulk limit of 500')
  })

  it('BulkFieldEditor sends only the ticked fields', async () => {
    const wrapper = mount(BulkFieldEditor, {
      props: {
        count: 2,
        fields: [
          { field: 'RiskScore', label: 'Risk Score', type: 'number' },
          { field: 'Description', label: 'Description', type: 'text' }
        ]
      }
    })
    await wrapper.find('input[aria-label="Change Risk Score"]').setValue(true)
    await wrapper.find('input[aria-label="Risk Score"]').setValue(700)
    await wrapper.find('form').trigger('submit')

    expect(wrapper.emitted('apply')[0][0]).toEqual({ fields: ['RiskScore'], riskScore: 700 })
  })

  it('useBulkAction posts the selected keys and keeps only skipped ones selected', async () => {
    const selection = useListSelectionStore('riskExceptions')
    selection.add([1, 2, 3])
    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse({
      requested: 3, changed: 2, skipped: [{ key: 3, name: 'EXC-3', reason: "It's already Revoked." }]
    }))
    const { run, result } = useBulkAction(selection)

    expect(await run('/api/risk-exceptions/bulk-revoke', { reason: 'x' }, 'revoked')).toBe(true)
    expect(JSON.parse(globalThis.fetch.mock.calls[0][1].body)).toEqual({ keys: [1, 2, 3], reason: 'x' })
    expect(result.value.verb).toBe('revoked')
    expect(selection.keys).toEqual([3])
  })

  it('useBulkAction reports a refused request', async () => {
    const selection = useListSelectionStore('targets')
    selection.add([1])
    globalThis.fetch = vi.fn().mockResolvedValue(jsonResponse({ detail: 'A reason is required.' }, false, 400))
    const { run, error } = useBulkAction(selection)

    expect(await run('/api/admin/targets/bulk-delete', { reason: '' }, 'deleted')).toBe(false)
    expect(error.value).toBe('A reason is required.')
    expect(selection.keys).toEqual([1])
  })
})
