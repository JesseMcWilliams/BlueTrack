import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import CyberArkApplications from './CyberArkApplications.vue'

// D-282: Reports > CyberArk Applications.
function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, headers: { get: () => null }, json: () => Promise.resolve(body) }
}

const app = (overrides = {}) => ({
  cyberArkApplicationKey: 1, appId: 'APP_Pay', description: 'Payroll', location: '\\Applications', disabled: false, expirationDate: null,
  accessPermittedFrom: 0, accessPermittedTo: 24, businessOwnerName: 'Olga Ray', businessOwnerEmail: 'o@example.com', isDeletedInSource: false,
  accountCount: 3, safeCount: 2,
  authentications: [
    { authType: 'machineAddress', authValue: '192.0.2.10' },
    { authType: 'hash', authValue: null },
    { authType: 'certificateattr', authValue: null, subject: 'CN=app', issuer: 'CN=ca' }
  ],
  ...overrides
})

describe('CyberArkApplications.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('lists each application with its methods, hours, status and what it can retrieve', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse([
      app(),
      app({ cyberArkApplicationKey: 2, appId: 'APP_Old', isDeletedInSource: true, accessPermittedFrom: 8, accessPermittedTo: 18, authentications: [], accountCount: 0, safeCount: 0 })
    ])))
    const wrapper = mount(CyberArkApplications)
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/reports/cyberark-applications')
    const rows = wrapper.findAll('tbody tr').map(r => r.findAll('td').map(td => td.text()))
    expect(rows[0][0]).toContain('APP_Pay')
    expect(rows[0][2]).toContain('Olga Ray')
    expect(rows[0].slice(3, 5)).toEqual(['Enabled', 'Any time'])
    expect(wrapper.findAll('tbody tr')[0].findAll('.methods li').map(li => li.text())).toEqual(['machineAddress: 192.0.2.10', 'hash', 'certificateattr: CN=app; CN=ca'])
    expect(rows[0].slice(6)).toEqual(['3', '2'])
    expect(rows[1].slice(3, 6)).toEqual(['Deleted in CyberArk', '8:00 to 18:00', 'None read'])
  })

  it('explains an empty list', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse([])))
    const wrapper = mount(CyberArkApplications)
    await flushPromises()
    expect(wrapper.text()).toContain('the PVWA user needs the Audit Users authorization')
  })
})
