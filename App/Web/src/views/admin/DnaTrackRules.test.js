import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import DnaTrackRules from './DnaTrackRules.vue'

vi.mock('../../composables/useConfirmDialog', () => ({ confirmDelete: vi.fn().mockResolvedValue(true) }))
import { confirmDelete } from '../../composables/useConfirmDialog'

// D-342: the DNA tracking rules on Data Sources.
function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, headers: { get: () => null }, json: () => Promise.resolve(body) }
}

const FIELDS = [
  { fieldName: 'Finding', kind: 'Text', operators: ['Equals', 'Contains', 'IsEmpty'] },
  { fieldName: 'DomainAccount', kind: 'YesNo', operators: ['Equals'] },
  { fieldName: 'LastLogin', kind: 'Date', operators: ['WithinDays', 'OlderThanDays'] }
]
const RULE = {
  dnaTrackRuleKey: 4, ruleName: 'Service accounts', isEnabled: true, description: null, matchingNow: 12,
  conditions: [{ fieldName: 'Finding', operator: 'Equals', value: 'Domain Scan' }, { fieldName: 'DomainAccount', operator: 'Equals', value: 'Yes' }]
}

function mountRules(rules = [RULE]) {
  globalThis.fetch = vi.fn((url, init) => {
    if (url === '/api/admin/dna/track-rules' && !init) return Promise.resolve(jsonResponse({ fields: FIELDS, rules }))
    if (url === '/api/admin/dna/track-rules/apply') return Promise.resolve(jsonResponse({ tracked: 3 }))
    if (init?.method === 'DELETE') return Promise.resolve(jsonResponse(null, true, 204))
    return Promise.resolve(jsonResponse({ dnaTrackRuleKey: 5 }, true, 201))
  })
  return mount(DnaTrackRules)
}

describe('DnaTrackRules.vue', () => {
  beforeEach(() => confirmDelete.mockResolvedValue(true))
  afterEach(() => vi.restoreAllMocks())

  it('lists each rule with its conditions and how many it would track now', async () => {
    const wrapper = mountRules()
    await flushPromises()

    const cells = wrapper.findAll('tbody td').map(td => td.text())
    expect(cells[0]).toBe('Service accounts')
    expect(wrapper.findAll('.conditions li').map(li => li.text())).toEqual(['Finding (sheet) is Domain Scan', 'Domain account is Yes'])
    expect(cells[2]).toBe('12')
  })

  it('creates a rule from its conditions, alphabetical fields, Yes/No offered as a choice', async () => {
    const wrapper = mountRules([])
    await flushPromises()
    expect(wrapper.text()).toContain('No DNA tracking rules.')

    await wrapper.findAll('button').find(b => b.text() === 'New tracking rule').trigger('click')
    const form = wrapper.find('form.dna-rule-form')
    await form.find('input[maxlength="200"]').setValue('Domain scan accounts')
    const [field] = form.findAll('.condition select')
    expect(field.findAll('option').map(o => o.text())).toEqual(['Domain account', 'Finding (sheet)', 'Last login'])
    await form.find('.condition input').setValue('Domain Scan')

    await wrapper.findAll('button').find(b => b.text() === 'Add condition').trigger('click')
    const second = wrapper.findAll('.condition')[1]
    await second.find('select').setValue('DomainAccount')
    await second.find('select').trigger('change')
    expect(second.findAll('select')[2].findAll('option').map(o => o.text())).toEqual(['Yes', 'No'])

    await form.trigger('submit')
    await flushPromises()
    const post = globalThis.fetch.mock.calls.find(([url, init]) => url === '/api/admin/dna/track-rules' && init?.method === 'POST')
    expect(JSON.parse(post[1].body)).toEqual({
      ruleName: 'Domain scan accounts', isEnabled: true, description: '',
      conditions: [{ fieldName: 'Finding', operator: 'Equals', value: 'Domain Scan' }, { fieldName: 'DomainAccount', operator: 'Equals', value: 'Yes' }]
    })
    expect(wrapper.text()).toContain('Tracking rule saved.')
  })

  it('applies the rules now, and deletes one after asking', async () => {
    const wrapper = mountRules()
    await flushPromises()

    await wrapper.findAll('button').find(b => b.text() === 'Apply the rules now').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('The rules tracked 3 accounts.')

    await wrapper.findAll('button').find(b => b.text() === 'Delete').trigger('click')
    await flushPromises()
    expect(globalThis.fetch.mock.calls.some(([url, init]) => url === '/api/admin/dna/track-rules/4' && init?.method === 'DELETE')).toBe(true)
  })
})
