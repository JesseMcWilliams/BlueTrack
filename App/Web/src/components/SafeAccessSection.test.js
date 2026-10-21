import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import SafeAccessSection from './SafeAccessSection.vue'

// D-281/D-282: who can use an account, and the CyberArk Applications that can retrieve it.
function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, headers: { get: () => null }, json: () => Promise.resolve(body) }
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/reports/cyberark-applications', name: 'reports-cyberark-applications', component: { template: '<div />' } }
    ]
  })
}

const mountSection = () => mount(SafeAccessSection, { props: { accountKey: 42 }, global: { plugins: [makeRouter()] } })

describe('SafeAccessSection.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('lists each person and way in, with their permissions, and the applications', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({
      people: [
        { personName: 'pat.lee', displayName: 'Pat Lee', isEnabled: true, accessPath: 'Direct', canUse: true, canRetrieve: false, canList: true },
        { personName: 'company.com\\ann', displayName: 'Ann Lee', isEnabled: false, accessPath: 'Via AD group: AD Admins', canUse: false, canRetrieve: true, canList: true }
      ],
      applications: [{ cyberArkApplicationKey: 5, appId: 'APP_Pay', description: 'Payroll', disabled: false, accessPath: 'Direct' }],
      adReadAt: '2026-10-21T02:00:00'
    })))
    const wrapper = mountSection()
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/account-progress/42/safe-access')
    expect(wrapper.find('summary').text()).toBe('2 people and ways in (show)')
    const rows = wrapper.findAll('tbody tr').map(r => r.findAll('td').map(td => td.text()))
    expect(rows[0]).toEqual(['pat.lee', 'Pat Lee', 'Direct', 'Yes', 'No', 'Yes'])
    expect(rows[1]).toEqual(['company.com\\ann (disabled in AD)', 'Ann Lee', 'Via AD group: AD Admins', 'No', 'Yes', 'Yes'])
    expect(wrapper.text()).toContain('AD group members as read by the Self-Hosted Direct pull at')
    const app = wrapper.find('li')
    expect(app.text()).toContain('APP_Pay (Payroll) — Direct')
    expect(app.find('a').attributes('href')).toBe('/reports/cyberark-applications')
  })

  it('says when there is no one and no application, and shows a failure', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({ people: [], applications: [], adReadAt: null })))
    const wrapper = mountSection()
    await flushPromises()
    expect(wrapper.text()).toContain("No member of this account's Safe is known.")
    expect(wrapper.text()).toContain('CyberArk Applications that can retrieve it')
    expect(wrapper.text()).not.toContain('AD group members as read')

    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse(null, { ok: false, status: 500 })))
    const failed = mountSection()
    await flushPromises()
    expect(failed.find('[role="alert"]').text()).toBe("Couldn't load who can use this account: Request failed: 500")
  })
})
