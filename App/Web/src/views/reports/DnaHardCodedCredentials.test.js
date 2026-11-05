import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import DnaHardCodedCredentials from './DnaHardCodedCredentials.vue'

// D-345: the hard-coded credentials DNA found, for holders of ViewDnaSensitiveColumns.
function jsonResponse(body, total) {
  return {
    ok: true,
    status: 200,
    headers: { get: (name) => (name === 'X-Total-Count' || name === 'X-Filtered-Count' ? String(total) : null) },
    json: () => Promise.resolve(body)
  }
}

const ROWS = [
  {
    dnaHardCodedKey: 1, sheetCode: 'Hard-Coded Credentials', machineName: 'APP01', machineType: 'Server', osVersion: 'Windows Server 2022',
    applicationServer: 'IIS', applicationName: 'Payroll', siteName: 'Default Web Site', accountName: 'svc_pay', hardCodedInFile: 'C:\\app\\web.config',
    credentialLocation: 'Line: 12 - "Server=DB01;User Id=svc_pay;Password=*****;"', passwordLength: 5, targetSystemAddress: 'DB01', targetSystemType: 'MSSQL', details: null
  }
]

describe('DnaHardCodedCredentials.vue', () => {
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => vi.restoreAllMocks())

  const listCalls = () => globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/reports/dna-hard-coded-credentials?'))

  it('lists where each hard-coded credential is, with DNA\'s masked line', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse(ROWS, 1)))
    const wrapper = mount(DnaHardCodedCredentials)
    await flushPromises()

    const cells = wrapper.findAll('tbody td').map(td => td.text())
    expect(cells[0]).toContain('APP01')
    expect(cells[1]).toContain('Payroll')
    expect(cells[3]).toBe('C:\\app\\web.config')
    expect(cells[4]).toBe('Line: 12 - "Server=DB01;User Id=svc_pay;Password=*****;"')
    expect(cells[5]).toBe('5')
    expect(cells[6]).toContain('DB01')
  })

  it('searches and sorts, and the export follows them', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse(ROWS, 1)))
    const wrapper = mount(DnaHardCodedCredentials)
    await flushPromises()

    await wrapper.find('input[type="search"]').setValue('web.config')
    await wrapper.find('form').trigger('submit')
    await wrapper.findAll('th').find(th => th.text().startsWith('Password length')).find('button').trigger('click')
    await flushPromises()

    const params = new URLSearchParams(listCalls().at(-1).split('?')[1])
    expect([params.get('search'), params.get('sort')]).toEqual(['web.config', 'PasswordLength:asc'])
    expect(wrapper.findAll('th').find(th => th.text().startsWith('Where in the file')).find('button').exists()).toBe(false)
  })

  it('says when there is nothing to show', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse([], 0)))
    const wrapper = mount(DnaHardCodedCredentials)
    await flushPromises()
    expect(wrapper.text()).toContain('The latest DNA workbook has no hard-coded credentials')
  })
})
