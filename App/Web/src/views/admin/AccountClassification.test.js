import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import AccountClassification from './AccountClassification.vue'
import AccountClassificationRuleEdit from './AccountClassificationRuleEdit.vue'

vi.mock('../../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../../composables/useConfirmDialog'

// D-233: Admin > Account Classification and its rule Add/Edit page.
function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const page = {
  settings: { isEnabled: true, defaultAccountClass: 'Unknown', ownerByEmployeeId: false, employeeIdAttribute: 'employeeID' },
  rules: [
    { accountClassificationRuleKey: 1, ruleType: 'Attribute', attributeName: 'adminCount', operator: 'Equals', value: '1', accountClass: 'Admin', isEnabled: true, displayOrder: 1 },
    { accountClassificationRuleKey: 2, ruleType: 'Name', attributeName: 'sAMAccountName', operator: 'StartsWith', value: 'adm.', accountClass: 'Admin', isEnabled: true, displayOrder: 1 },
    { accountClassificationRuleKey: 3, ruleType: 'Name', attributeName: 'sAMAccountName', operator: 'EndsWith', value: '-a', accountClass: 'Secondary', isEnabled: false, displayOrder: 2 }
  ],
  allowedAttributes: ['adminCount', 'employeeType', 'extensionAttribute5'],
  nameAttributes: ['sAMAccountName', 'userPrincipalName'],
  employeeIdAttributes: ['employeeID', 'employeeNumber'],
  ruleClasses: ['Primary', 'Secondary', 'Admin']
}

function byLabel(wrapper, label) {
  const button = wrapper.findAll('button').find(b => b.attributes('aria-label') === label)
  if (!button) throw new Error(`No button labelled ${label}`)
  return button
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/admin/account-classification', name: 'admin-account-classification', component: { template: '<div />' } },
      { path: '/admin/account-classification/rules/new', name: 'admin-account-classification-rule-create', component: { template: '<div />' } },
      { path: '/admin/account-classification/rules/:ruleKey', name: 'admin-account-classification-rule-edit', component: { template: '<div />' } }
    ]
  })
}

function mockApi(overrides = {}) {
  globalThis.fetch = vi.fn((url, options = {}) => {
    const key = `${options.method ?? 'GET'} ${url}`
    if (overrides[key]) return Promise.resolve(overrides[key])
    if (key === 'GET /api/admin/account-classification') return Promise.resolve(jsonResponse(page))
    if (key === 'PUT /api/admin/account-classification/settings') return Promise.resolve(jsonResponse({ reclassified: 1234 }))
    if (key === 'POST /api/admin/account-classification/reclassify') return Promise.resolve(jsonResponse({ reclassified: 5 }))
    if (key.startsWith('DELETE ') || key.endsWith('/move')) return Promise.resolve({ ok: true, status: 204, json: () => Promise.resolve(null) })
    if (key === 'POST /api/admin/account-classification/rules') return Promise.resolve(jsonResponse({ accountClassificationRuleKey: 9 }, { status: 201 }))
    if (key.startsWith('PUT /api/admin/account-classification/rules/')) return Promise.resolve(jsonResponse({ reclassified: 1 }))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

describe('AccountClassification.vue', () => {
  beforeEach(() => {
    confirmDelete.mockResolvedValue(true)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows the settings and each rule type in its own table, in order', async () => {
    mockApi()
    const wrapper = mount(AccountClassification, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.get('input[type="checkbox"]').element.checked).toBe(true)
    const headings = wrapper.findAll('h3').map(h => h.text())
    expect(headings).toEqual(['Attribute Rules', 'Name Rules', 'OU Rules'])
    expect(wrapper.text()).toContain('adminCount equals "1"')
    expect(wrapper.text()).toContain('sAMAccountName starts with "adm."')
    expect(wrapper.text()).toContain('No rules of this type.')
    const nameRows = wrapper.findAll('section')[1].findAll('tbody tr').map(r => r.findAll('td').map(td => td.text()))
    expect(nameRows.map(r => [r[0], r[2], r[3]])).toEqual([['1', 'Admin', 'Yes'], ['2', 'Secondary', 'No']])
  })

  it('saves the settings and says how many accounts were reclassified', async () => {
    mockApi()
    const wrapper = mount(AccountClassification, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const checkboxes = wrapper.findAll('input[type="checkbox"]')
    await checkboxes[1].setValue(true)
    await wrapper.findAll('select')[1].setValue('employeeNumber')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const put = globalThis.fetch.mock.calls.find(c => c[0] === '/api/admin/account-classification/settings')
    expect(JSON.parse(put[1].body)).toMatchObject({ isEnabled: true, ownerByEmployeeId: true, employeeIdAttribute: 'employeeNumber' })
    expect(wrapper.text()).toContain('Settings saved. 1,234 discovered accounts reclassified.')
  })

  it('moves a rule, deletes one after confirming, and reclassifies on request', async () => {
    mockApi()
    const wrapper = mount(AccountClassification, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const upButton = byLabel(wrapper, 'Move sAMAccountName ends with "-a" up')
    await upButton.trigger('click')
    await flushPromises()
    const move = globalThis.fetch.mock.calls.find(c => c[0] === '/api/admin/account-classification/rules/3/move')
    expect(JSON.parse(move[1].body)).toEqual({ direction: 'up' })
    // The first rule of a type can't move up.
    expect(byLabel(wrapper, 'Move sAMAccountName starts with "adm." up').attributes('disabled')).toBeDefined()

    await byLabel(wrapper, 'Delete adminCount equals "1"').trigger('click')
    await flushPromises()
    expect(confirmDelete).toHaveBeenCalledWith(expect.stringContaining('adminCount equals "1"'))
    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/account-classification/rules/1', { method: 'DELETE' })

    await wrapper.findAll('button').find(b => b.text() === 'Reclassify Now').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('5 discovered accounts reclassified.')
  })

  it('does not delete when the confirm is declined, and shows a server error', async () => {
    confirmDelete.mockResolvedValueOnce(false)
    mockApi({ 'POST /api/admin/account-classification/reclassify': jsonResponse({ detail: 'Nope.' }, { ok: false, status: 500 }) })
    const wrapper = mount(AccountClassification, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    await byLabel(wrapper, 'Delete adminCount equals "1"').trigger('click')
    await flushPromises()
    expect(globalThis.fetch.mock.calls.some(c => c[1]?.method === 'DELETE')).toBe(false)

    await wrapper.findAll('button').find(b => b.text() === 'Reclassify Now').trigger('click')
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toBe('Nope.')
  })
})

describe('AccountClassificationRuleEdit.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('new: offers only the allow-listed attributes, and posts the rule', async () => {
    mockApi()
    const router = makeRouter()
    await router.push({ name: 'admin-account-classification-rule-create', query: { type: 'Attribute' } })
    const wrapper = mount(AccountClassificationRuleEdit, { global: { plugins: [router] } })
    await flushPromises()

    expect(wrapper.text()).toContain('New Classification Rule')
    const selects = wrapper.findAll('select')
    expect(selects[1].findAll('option').map(o => o.text())).toEqual(['adminCount', 'employeeType', 'extensionAttribute5'])
    await selects[1].setValue('extensionAttribute5')
    await wrapper.get('input:not([type])').setValue('Admin')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const post = globalThis.fetch.mock.calls.find(c => c[0] === '/api/admin/account-classification/rules')
    expect(JSON.parse(post[1].body)).toMatchObject({ ruleType: 'Attribute', attributeName: 'extensionAttribute5', operator: 'Equals', value: 'Admin', accountClass: 'Admin', isEnabled: true })
    expect(router.currentRoute.value.name).toBe('admin-account-classification')
  })

  it('new name rule: Is set is not offered, and a pattern can be chosen', async () => {
    mockApi()
    const router = makeRouter()
    await router.push({ name: 'admin-account-classification-rule-create', query: { type: 'Name' } })
    const wrapper = mount(AccountClassificationRuleEdit, { global: { plugins: [router] } })
    await flushPromises()

    const operatorSelect = wrapper.findAll('select')[2]
    expect(operatorSelect.findAll('option').map(o => o.text())).toEqual(['starts with', 'ends with', 'matches pattern'])
  })

  it('edit: pre-fills the rule, shows a 400 detail and stays on the page', async () => {
    mockApi({ 'PUT /api/admin/account-classification/rules/2': jsonResponse({ detail: 'Value is required.' }, { ok: false, status: 400 }) })
    const router = makeRouter()
    await router.push({ name: 'admin-account-classification-rule-edit', params: { ruleKey: 2 } })
    const wrapper = mount(AccountClassificationRuleEdit, { props: { ruleKey: '2' }, global: { plugins: [router] } })
    await flushPromises()

    expect(wrapper.text()).toContain('Edit Classification Rule')
    expect(wrapper.get('input:not([type])').element.value).toBe('adm.')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toBe('Value is required.')
    expect(router.currentRoute.value.name).toBe('admin-account-classification-rule-edit')
  })
})
