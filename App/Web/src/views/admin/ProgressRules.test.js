import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import ProgressRules from './ProgressRules.vue'
import ProgressRuleEdit from './ProgressRuleEdit.vue'
import BlueprintStages from './BlueprintStages.vue'

// D-283/D-284: Admin > Progress Rules, the rule form (with Test), and Admin > Blueprint Stages.
vi.mock('../../composables/useConfirmDialog', () => ({ confirmDelete: vi.fn().mockResolvedValue(true) }))
import { confirmDelete } from '../../composables/useConfirmDialog'

function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, headers: { get: () => null }, json: () => Promise.resolve(body) }
}

function makeRouter() {
  const page = { template: '<div />' }
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: page },
      { path: '/admin/progress-rules', name: 'admin-progress-rules', component: page },
      { path: '/admin/progress-rules/new', name: 'admin-progress-rule-create', component: page },
      { path: '/admin/progress-rules/:ruleKey', name: 'admin-progress-rule-edit', component: page },
      { path: '/admin/blueprint-stages', name: 'admin-blueprint-stages', component: page },
      { path: '/accounts/:accountKey', name: 'account-progress-detail', component: page }
    ]
  })
}

const stages = [
  { stageKey: 1, stageName: 'Discovered', stageOrder: 1, isStartStage: true, milestone: null, accountCount: 10 },
  { stageKey: 2, stageName: 'Assessed / Prioritized', stageOrder: 2, isStartStage: false, milestone: null, accountCount: 0 },
  { stageKey: 3, stageName: 'Onboarded to Vault', stageOrder: 3, isStartStage: false, milestone: 'Onboarded', accountCount: 90 }
]
const fields = [
  { fieldName: 'CPMStatus', kind: 'Text', operators: ['Equals', 'NotEquals', 'IsEmpty'] },
  { fieldName: 'AutoManaged', kind: 'YesNo', operators: ['Equals', 'IsEmpty'] },
  { fieldName: 'LastVerified', kind: 'Date', operators: ['WithinDays', 'IsEmpty'] }
]
const rule = {
  progressRuleKey: 7, ruleName: 'In the Vault', fromStageKey: 1, fromStageName: 'Discovered', toStageKey: 3, toStageName: 'Onboarded to Vault',
  isEnabled: true, description: null,
  conditions: [{ fieldName: 'CPMStatus', operator: 'Equals', value: 'success' }, { fieldName: 'LastVerified', operator: 'IsEmpty', value: null }]
}

describe('Progress Rules and Blueprint Stages', () => {
  beforeEach(() => confirmDelete.mockResolvedValue(true))
  afterEach(() => vi.restoreAllMocks())

  it('lists each rule with its stages and conditions, and deletes after asking', async () => {
    globalThis.fetch = vi.fn((url, init) => Promise.resolve(init?.method === 'DELETE' ? jsonResponse(null, { status: 204 }) : jsonResponse([rule])))
    const wrapper = mount(ProgressRules, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const cells = wrapper.findAll('tbody td').map(td => td.text())
    expect(cells[0]).toBe('In the Vault')
    expect(cells[1]).toBe('from Discovered to Onboarded to Vault')
    expect(wrapper.findAll('.conditions li').map(li => li.text())).toEqual(['CPM status is success', 'Last verified is empty'])

    await wrapper.findAll('button').find(b => b.text() === 'Delete').trigger('click')
    await flushPromises()
    expect(confirmDelete).toHaveBeenCalledWith(expect.stringContaining('In the Vault'))
    expect(globalThis.fetch.mock.calls.some(([url, init]) => url === '/api/admin/progress-rules/7' && init?.method === 'DELETE')).toBe(true)
  })

  it('a new rule: To follows From, a Yes/No field offers Yes and No, and Test shows what would move', async () => {
    globalThis.fetch = vi.fn((url, init) => {
      if (url === '/api/admin/blueprint-stages') return Promise.resolve(jsonResponse(stages))
      if (url === '/api/admin/progress-rules/fields') return Promise.resolve(jsonResponse(fields))
      if (url === '/api/admin/progress-rules/preview') return Promise.resolve(jsonResponse({ matchCount: 2, accounts: [{ accountKey: 5, accountName: 'root@host', safeName: 'Unix' }] }))
      if (init?.method === 'POST') return Promise.resolve(jsonResponse({ progressRuleKey: 8 }, { status: 201 }))
      return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
    })
    const router = makeRouter()
    const wrapper = mount(ProgressRuleEdit, { global: { plugins: [router] } })
    await flushPromises()

    const [from, to] = wrapper.findAll('select').slice(0, 2)
    expect(from.element.value).toBe('1')
    expect(to.findAll('option').map(o => o.text())).toEqual(['Assessed / Prioritized', 'Onboarded to Vault'])
    await to.setValue('3')
    await wrapper.find('input[maxlength="200"]').setValue('Managed')

    const fieldSelect = wrapper.find('.condition select')
    await fieldSelect.setValue('AutoManaged')
    await fieldSelect.trigger('change')
    expect(wrapper.findAll('.condition select')[2].findAll('option').map(o => o.text())).toEqual(['Yes', 'No'])

    await wrapper.findAll('button').find(b => b.text() === 'Test').trigger('click')
    await flushPromises()
    const preview = JSON.parse(globalThis.fetch.mock.calls.find(([url]) => url === '/api/admin/progress-rules/preview')[1].body)
    expect(preview).toMatchObject({ fromStageKey: 1, toStageKey: 3, conditions: [{ fieldName: 'AutoManaged', operator: 'Equals', value: 'Yes' }] })
    expect(wrapper.find('.preview').text()).toContain('2 accounts at the From stage would move now')
    expect(wrapper.find('.preview a').attributes('href')).toBe('/accounts/5')

    await wrapper.find('form').trigger('submit')
    await flushPromises()
    const saved = globalThis.fetch.mock.calls.find(([url, init]) => url === '/api/admin/progress-rules' && init?.method === 'POST')
    expect(JSON.parse(saved[1].body)).toMatchObject({ ruleName: 'Managed', fromStageKey: 1, toStageKey: 3, isEnabled: true })
  })

  it('stages: saves a row, moves one, shows a refusal, and adds one at the end', async () => {
    globalThis.fetch = vi.fn((url, init) => {
      if (init?.method === 'DELETE') return Promise.resolve(jsonResponse({ detail: "'Discovered' can't be deleted: new accounts start at it." }, { ok: false, status: 409 }))
      if (init?.method) return Promise.resolve(jsonResponse(null, { status: 204 }))
      return Promise.resolve(jsonResponse(stages))
    })
    const wrapper = mount(BlueprintStages, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const rows = wrapper.findAll('tbody tr')
    await rows[2].find('input').setValue('In the Vault')
    await rows[2].findAll('button').find(b => b.text() === 'Save').trigger('click')
    await flushPromises()
    const put = globalThis.fetch.mock.calls.find(([url, init]) => url === '/api/admin/blueprint-stages/3' && init?.method === 'PUT')
    expect(JSON.parse(put[1].body)).toEqual({ stageName: 'In the Vault', isStartStage: false, milestone: 'Onboarded' })

    await wrapper.findAll('tbody tr')[1].findAll('button').find(b => b.text() === 'Move up').trigger('click')
    await flushPromises()
    const order = globalThis.fetch.mock.calls.find(([url]) => url === '/api/admin/blueprint-stages/order')
    expect(JSON.parse(order[1].body)).toEqual({ stageKeys: [2, 1, 3] })

    await wrapper.findAll('tbody tr')[0].findAll('button').find(b => b.text() === 'Delete').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toBe("'Discovered' can't be deleted: new accounts start at it.")

    await wrapper.find('form.add input').setValue('Owner confirmed')
    await wrapper.find('form.add').trigger('submit')
    await flushPromises()
    const post = globalThis.fetch.mock.calls.find(([url, init]) => url === '/api/admin/blueprint-stages' && init?.method === 'POST')
    expect(JSON.parse(post[1].body)).toEqual({ stageName: 'Owner confirmed' })
  })
})
