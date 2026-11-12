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

  it('lists direct members, then each group with its rights, opening to its members, and the applications', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({
      people: [
        { personName: 'pat.lee', displayName: 'Pat Lee', isEnabled: true, accessPath: 'Direct', canUse: true, canRetrieve: false, canList: true, groupKey: null, userSource: 'Vault' },
        { personName: 'company.com\\ann', displayName: 'Ann Lee', isEnabled: false, accessPath: 'Via AD group: AD Admins', canUse: false, canRetrieve: true, canList: true, groupKey: 7, userSource: 'LDAP' },
        { personName: 'company.com\\bob', displayName: 'Bob Ray', isEnabled: true, accessPath: 'Via AD group: AD Admins > Team A', canUse: false, canRetrieve: true, canList: true, groupKey: 7, userSource: 'LDAP' }
      ],
      groups: [{ groupKey: 7, groupName: 'AD Admins', canUse: false, canRetrieve: true, canList: true, adReadStatus: 'Found', adReadMessage: null, adDomainName: 'company.com' }],
      applications: [{ cyberArkApplicationKey: 5, appId: 'APP_Pay', description: 'Payroll', disabled: false, accessPath: 'Direct' }],
      adReadAt: '2026-10-21T02:00:00'
    })))
    const wrapper = mountSection()
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/account-progress/42/safe-access')
    expect(wrapper.find('summary').text()).toBe('1 direct member(s) and 1 group(s) (show)')
    const cells = row => row.findAll('td').map(td => td.text())
    expect(cells(wrapper.find('tr.direct-member'))).toEqual(['pat.lee', 'Pat Lee', 'Vault', 'Direct', 'Yes', 'No', 'Yes'])
    const group = wrapper.find('tr.group-row')
    expect(cells(group)).toEqual(['\u25b8 AD Admins (2 members)', 'AD group', 'No', 'Yes', 'Yes'])
    expect(wrapper.findAll('tr.group-member')).toHaveLength(0)

    const toggle = group.find('button.group-toggle')
    expect(toggle.attributes('aria-expanded')).toBe('false')
    await toggle.trigger('click')
    expect(toggle.attributes('aria-expanded')).toBe('true')
    const members = wrapper.findAll('tr.group-member').map(cells)
    expect(members).toEqual([
      ['company.com\\ann (disabled in AD)', 'Ann Lee', 'LDAP', '', 'No', 'Yes', 'Yes'],
      ['company.com\\bob', 'Bob Ray', 'LDAP', 'through Team A', 'No', 'Yes', 'Yes']
    ])
    await toggle.trigger('click')
    expect(wrapper.findAll('tr.group-member')).toHaveLength(0)

    expect(wrapper.text()).toContain('AD group members as read by the Self-Hosted Direct pull at')
    const app = wrapper.find('li')
    expect(app.text()).toContain('APP_Pay (Payroll) — Direct')
    expect(app.find('a').attributes('href')).toBe('/reports/cyberark-applications')
  })

  it('still lists a group whose AD members were not read, saying why, and a Vault group', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({
      people: [
        { personName: 'vault.user', displayName: null, isEnabled: true, accessPath: 'Via Vault group: Vault Ops', canUse: true, canRetrieve: true, canList: true, groupKey: 3 }
      ],
      groups: [
        { groupKey: 9, groupName: 'Gone Group', canUse: true, canRetrieve: false, canList: true, adReadStatus: 'NotFound', adReadMessage: null },
        { groupKey: 8, groupName: 'Flaky Group', canUse: true, canRetrieve: false, canList: true, adReadStatus: 'Failed', adReadMessage: 'LDAP timeout' },
        { groupKey: 3, groupName: 'Vault Ops', canUse: true, canRetrieve: true, canList: true, adReadStatus: null, adReadMessage: null }
      ],
      applications: [],
      adReadAt: null
    })))
    const wrapper = mountSection()
    await flushPromises()

    const rows = wrapper.findAll('tr.group-row')
    expect(rows).toHaveLength(3)
    expect(rows[0].text()).toContain('Gone Group (no members listed)')
    expect(rows[0].find('.group-note').text()).toBe('Not found in AD, and the Vault lists no members for it.')
    expect(rows[0].find('button').exists()).toBe(false)
    expect(rows[1].find('.group-note').text()).toContain("couldn't be read at the last pull (LDAP timeout)")
    expect(rows[2].text()).toContain('Vault group')
    expect(rows[2].text()).toContain('Vault Ops (1 member)')
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
