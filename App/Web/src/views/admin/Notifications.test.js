import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import Notifications from './Notifications.vue'

// D-222: the notification types' Enabled checkbox and the Schedule section.
vi.mock('vue-router', () => ({ useRouter: () => ({ push: vi.fn() }) }))

function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const types = [
  { notificationTypeKey: 1, notificationTypeName: 'DevFakeAuthEnabledTooLong', description: 'DevFakeAuth on too long', targetRoleKey: null, isEnabled: true },
  { notificationTypeKey: 4, notificationTypeName: 'NightlyImportFailed', description: 'Import and Load failed', targetRoleKey: null, isEnabled: false },
  { notificationTypeKey: 5, notificationTypeName: 'OverdueRiskExceptions', description: 'Daily overdue digest', targetRoleKey: null, isEnabled: false },
  { notificationTypeKey: 6, notificationTypeName: 'WeeklySummary', description: 'Weekly summary', targetRoleKey: null, isEnabled: false }
]
const schedule = { dailyDigestTime: '07:00', weeklySummaryDay: 'Mon', weeklySummaryTime: '07:30' }

function respond(overrides = {}) {
  return vi.fn((url, init) => {
    const method = init?.method ?? 'GET'
    const key = `${method} ${url}`
    if (overrides[key]) return Promise.resolve(overrides[key])
    if (method !== 'GET') return Promise.resolve(jsonResponse(null, true, 204))
    if (url === '/api/admin/notifications/config') return Promise.resolve(jsonResponse({ smtpPort: 587, authMethod: 'None', enableStartTls: true }))
    if (url === '/api/admin/notifications/types') return Promise.resolve(jsonResponse(types.map(t => ({ ...t })))) // copies: the page edits what it is given
    if (url === '/api/admin/notifications/schedule') return Promise.resolve(jsonResponse({ ...schedule }))
    return Promise.resolve(jsonResponse([]))
  })
}

describe('Notifications.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('lists the new notification types with their Enabled state, and the schedule', async () => {
    globalThis.fetch = respond()
    const wrapper = mount(Notifications, { global: { stubs: { 'router-link': true } } })
    await flushPromises()

    for (const name of ['NightlyImportFailed', 'OverdueRiskExceptions', 'WeeklySummary']) {
      expect(wrapper.text()).toContain(name)
      expect(wrapper.find(`input[aria-label="${name} enabled"]`).element.checked).toBe(false)
    }
    expect(wrapper.find('input[aria-label="DevFakeAuthEnabledTooLong enabled"]').element.checked).toBe(true)
    expect(wrapper.find('input[type="time"]').element.value).toBe('07:00')
    expect(wrapper.find('select option[value="Sun"]').exists()).toBe(true)
  })

  it('turning a type on PUTs it to its enabled endpoint', async () => {
    globalThis.fetch = respond()
    const wrapper = mount(Notifications, { global: { stubs: { 'router-link': true } } })
    await flushPromises()

    await wrapper.find('input[aria-label="WeeklySummary enabled"]').setValue(true)
    await flushPromises()

    const put = globalThis.fetch.mock.calls.find(([url, init]) => url === '/api/admin/notifications/types/6/enabled' && init?.method === 'PUT')
    expect(JSON.parse(put[1].body)).toEqual({ isEnabled: true })
  })

  // UI review W9-1
  it('toggling a type reloads only the types, so unsaved SMTP edits stay', async () => {
    globalThis.fetch = respond()
    const wrapper = mount(Notifications, { global: { stubs: { 'router-link': true } } })
    await flushPromises()
    await wrapper.find('input[placeholder]').setValue('smtp.example.com')
    globalThis.fetch.mockClear()

    await wrapper.find('input[aria-label="WeeklySummary enabled"]').setValue(true)
    await flushPromises()

    const gets = globalThis.fetch.mock.calls.filter(([, init]) => !init?.method).map(c => c[0])
    expect(gets).toEqual(['/api/admin/notifications/types'])
    expect(wrapper.find('input[placeholder]').element.value).toBe('smtp.example.com')
  })

  // UI review W9-3
  it('Send test email waits for the SMTP settings to be saved', async () => {
    globalThis.fetch = respond()
    const wrapper = mount(Notifications, { global: { stubs: { 'router-link': true } } })
    await flushPromises()
    const testButton = () => wrapper.findAll('button').find(b => b.text() === 'Send Test Email')
    expect(testButton().attributes('disabled')).toBeUndefined()

    await wrapper.find('input[placeholder]').setValue('smtp.example.com')
    expect(testButton().attributes('disabled')).toBeDefined()
    expect(wrapper.text()).toContain('Save your changes first.')
  })

  // D-223: OAuth2 auth modes.
  function smtpForm(wrapper) {
    return wrapper.findAll('form').find(f => f.text().includes('Auth Method'))
  }
  function labels(form) {
    return form.findAll('.field-label-text').map(l => l.text())
  }

  it('shows only the selected auth mode\'s fields', async () => {
    globalThis.fetch = respond()
    const wrapper = mount(Notifications, { global: { stubs: { 'router-link': true } } })
    await flushPromises()
    const form = smtpForm(wrapper)
    const method = form.find('select')

    expect(labels(form)).not.toContain('Tenant ID:')
    expect(labels(form)).not.toContain('Credential:')

    await method.setValue('Basic')
    expect(labels(form)).toContain('Credential:')
    expect(labels(form)).not.toContain('Sign-in mailbox:')

    await method.setValue('Microsoft365')
    expect(labels(form)).toEqual(expect.arrayContaining(['Tenant ID:', 'Client (application) ID:', 'Client credential type:', 'Client secret credential:', 'Sign-in mailbox:']))
    expect(labels(form)).not.toContain('Token URL:')
    expect(form.find('input[placeholder="smtp.company.com"]').element.value).toBe('smtp.office365.com')
    expect(form.text()).toContain('SMTP.SendAsApp')

    const credentialType = form.findAll('select').find(s => s.find('option[value="Certificate"]').exists())
    await credentialType.setValue('Certificate')
    expect(labels(form)).toContain('Certificate and key credential:')

    await method.setValue('GoogleWorkspace')
    expect(labels(form)).toEqual(expect.arrayContaining(['Service account key credential:', 'Sign-in mailbox:']))
    expect(labels(form)).not.toContain('Tenant ID:')
    expect(labels(form)).not.toContain('Client ID:')

    await method.setValue('GenericOAuth2')
    expect(labels(form)).toEqual(expect.arrayContaining(['Token URL:', 'Client ID:', 'Client authentication:', 'Scope (optional):', 'Audience (optional):', 'XOAUTH2 username:']))
    expect(labels(form)).not.toContain('Tenant ID:')

    await method.setValue('None')
    expect(labels(form)).not.toContain('Token URL:')
    expect(labels(form)).not.toContain('Credential:')
  })

  it('saves only the selected mode\'s OAuth2 fields, and shows the server\'s reason when refused', async () => {
    globalThis.fetch = respond({
      'PUT /api/admin/notifications/config': jsonResponse({ detail: 'Token endpoint must be https.' }, false, 400)
    })
    const wrapper = mount(Notifications, { global: { stubs: { 'router-link': true } } })
    await flushPromises()
    const form = smtpForm(wrapper)

    await form.find('select').setValue('Microsoft365')
    await form.find('input[placeholder="contoso.onmicrosoft.com or GUID"]').setValue('contoso.onmicrosoft.com')
    await form.find('select').setValue('GenericOAuth2')
    await form.find('input[type="url"]').setValue('https://idp.example.com/token')
    await form.trigger('submit')
    await flushPromises()

    const put = globalThis.fetch.mock.calls.find(([url, init]) => url === '/api/admin/notifications/config' && init?.method === 'PUT')
    const body = JSON.parse(put[1].body)
    expect(body.authMethod).toBe('GenericOAuth2')
    expect(body.oAuthTokenUrl).toBe('https://idp.example.com/token')
    expect(body.oAuthClientAuthStyle).toBe('Basic')
    expect(body.oAuthTenantId).toBeNull()
    expect(body.oAuthClientCredentialType).toBeNull()
    expect(body.enableStartTls).toBe(true)
    expect(wrapper.text()).toContain('Save failed: 400 -- Token endpoint must be https.')
  })

  it('saves the schedule, and shows the server\'s reason when it is refused', async () => {
    globalThis.fetch = respond({
      'PUT /api/admin/notifications/schedule': jsonResponse({ detail: 'WeeklySummaryDay must be one of Sun, Mon, Tue, Wed, Thu, Fri, Sat.' }, false, 400)
    })
    const wrapper = mount(Notifications, { global: { stubs: { 'router-link': true } } })
    await flushPromises()

    const scheduleForm = wrapper.findAll('form').find(f => f.text().includes('Save Schedule'))
    await scheduleForm.find('input[type="time"]').setValue('06:45')
    await scheduleForm.trigger('submit')
    await flushPromises()

    const put = globalThis.fetch.mock.calls.find(([url, init]) => url === '/api/admin/notifications/schedule' && init?.method === 'PUT')
    expect(JSON.parse(put[1].body)).toEqual({ dailyDigestTime: '06:45', weeklySummaryDay: 'Mon', weeklySummaryTime: '07:30' })
    expect(wrapper.text()).toContain('Save failed: 400 -- WeeklySummaryDay must be one of')
  })

  // UI review W9-6: a refused target-role change left the select on the role that was not saved.
  it('a refused target-role change shows the error and puts the select back on the saved role', async () => {
    globalThis.fetch = respond({
      'GET /api/admin/roles': jsonResponse([{ appRoleKey: 3, roleName: 'Admin' }]),
      'PUT /api/admin/notifications/types/6/target-role': jsonResponse(null, false, 500)
    })
    const wrapper = mount(Notifications, { global: { stubs: { 'router-link': true } } })
    await flushPromises()
    const row = () => wrapper.findAll('tbody tr').find(r => r.text().includes('WeeklySummary'))

    const select = row().find('select')
    select.element.value = '3'
    await select.trigger('change')
    await flushPromises()

    expect(wrapper.text()).toContain('Save failed: 500')
    expect(row().find('select').element.selectedIndex).toBe(0) // reloaded: back on the "none" option, nothing was saved
  })

  it('a target-role change that is saved sends the role and shows no error', async () => {
    globalThis.fetch = respond({ 'GET /api/admin/roles': jsonResponse([{ appRoleKey: 3, roleName: 'Admin' }]) })
    const wrapper = mount(Notifications, { global: { stubs: { 'router-link': true } } })
    await flushPromises()

    const select = wrapper.findAll('tbody tr').find(r => r.text().includes('WeeklySummary')).find('select')
    select.element.value = '3'
    await select.trigger('change')
    await flushPromises()

    const put = globalThis.fetch.mock.calls.find(([url, init]) => url === '/api/admin/notifications/types/6/target-role' && init?.method === 'PUT')
    expect(JSON.parse(put[1].body)).toEqual({ targetRoleKey: 3 })
    expect(wrapper.text()).not.toContain('Save failed')
  })
})
