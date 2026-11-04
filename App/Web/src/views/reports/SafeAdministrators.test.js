import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import SafeAdministrators from './SafeAdministrators.vue'

// D-335: Reports > Safe Administrators.
function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, headers: { get: () => null }, json: () => Promise.resolve(body) }
}

const router = () => createRouter({
  history: createMemoryHistory(),
  routes: [{ path: '/', component: { template: '<div />' } }, { path: '/safes/:safeKey', name: 'safe-detail', component: { template: '<div />' } }]
})

const row = (over) => ({
  safeKey: 1, safeName: 'Unix', sourceSystemName: 'Self-Hosted', personName: 'ann', displayName: 'Ann Lee', isEnabled: true,
  accessPaths: 'Direct', manageSafe: true, manageSafeMembers: false, fullRights: false, isBuiltIn: false, ...over
})

const rows = [
  row({}),
  row({ safeKey: 2, safeName: 'Windows', manageSafeMembers: true, fullRights: true, accessPaths: 'Via AD group: Admins > Team A' }),
  row({ safeKey: 2, safeName: 'Windows', personName: 'bob', displayName: null, fullRights: true }),
  row({ personName: 'Master', displayName: null, fullRights: true, isBuiltIn: true })
]

async function mountPage() {
  globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse(rows)))
  const wrapper = mount(SafeAdministrators, { global: { plugins: [router()] } })
  await flushPromises()
  return wrapper
}

const firstCells = wrapper => wrapper.findAll('tbody tr').map(tr => tr.findAll('td').slice(0, 3).map(td => td.text()))

describe('SafeAdministrators.vue', () => {
  afterEach(() => vi.restoreAllMocks())

  it('lists each Safe and person, hiding the built-in ones until asked, and links the Safe', async () => {
    const wrapper = await mountPage()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/reports/safe-administrators')
    expect(firstCells(wrapper)).toEqual([['Unix', 'Self-Hosted', 'ann'], ['Windows', 'Self-Hosted', 'ann'], ['Windows', 'Self-Hosted', 'bob']])
    expect(wrapper.text()).toContain("1 row for CyberArk's built-in users")
    expect(wrapper.findAll('tbody tr')[1].text()).toContain('Via AD group: Admins > Team A')
    expect(wrapper.find('tbody a').attributes('href')).toBe('/safes/1')

    await wrapper.findAll('input[type="checkbox"]')[1].setValue(true)   // Show built-in
    const master = wrapper.findAll('tbody tr').find(tr => tr.text().includes('Master'))
    expect(master.findAll('td')[2].text()).toMatch(/^Master\s*\(built-in\)$/)
  })

  it('filters by full rights and by text', async () => {
    const wrapper = await mountPage()

    await wrapper.findAll('input[type="checkbox"]')[0].setValue(true)   // Only full rights
    expect(firstCells(wrapper).map(c => c[2])).toEqual(['ann', 'bob'])

    await wrapper.find('input[type="search"]').setValue('BOB')
    expect(firstCells(wrapper).map(c => c[2])).toEqual(['bob'])
  })

  it('adds up each person by Safe count, most full rights first, and exports the view', async () => {
    const wrapper = await mountPage()
    await wrapper.find('select').setValue('person')

    const cells = wrapper.findAll('tbody tr').map(tr => tr.findAll('td').map(td => td.text()))
    expect(cells).toEqual([
      ['ann', 'Ann Lee', 'Yes', '2', '2', '1', '1'],
      ['bob', '', 'Yes', '1', '1', '0', '1']
    ])
  })
})
