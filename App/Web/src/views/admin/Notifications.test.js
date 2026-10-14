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
    if (url === '/api/admin/notifications/types') return Promise.resolve(jsonResponse(types))
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
})
