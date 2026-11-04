import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import PowerBiAccess from './PowerBiAccess.vue'

// D-314: Admin > Power BI shows where to connect, what to import and relate, and a read-access script.
const ACCESS = {
  serverName: 'SQL01\\BT', databaseName: 'BlueTrack',
  objects: [
    { schema: 'dbo', name: 'vw_powerbi_accounts', kind: 'Fact', description: 'One row per account.', columns: ['AccountKey', 'SafeKey'] },
    { schema: 'dbo', name: 'dim_safe', kind: 'Dimension', description: 'Safes.', columns: ['SafeKey', 'SafeName'] }
  ],
  relationships: [{ fromObject: 'vw_powerbi_accounts', fromColumn: 'SafeKey', toObject: 'dim_safe', toColumn: 'SafeKey' }]
}

async function mountPage(response = { ok: true, status: 200, json: () => Promise.resolve(ACCESS) }) {
  globalThis.fetch = vi.fn(() => Promise.resolve(response))
  const wrapper = mount(PowerBiAccess)
  await flushPromises()
  return wrapper
}

describe('PowerBiAccess.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('names the server and database, the objects with their columns, and the relationships', async () => {
    const wrapper = await mountPage()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/admin/power-bi')
    expect(wrapper.find('.power-bi-connect').text()).toContain('SQL01\\BT')
    expect(wrapper.find('.power-bi-connect').text()).toContain('BlueTrack')
    expect(wrapper.text()).toContain('dbo.vw_powerbi_accounts')
    expect(wrapper.text()).toContain('AccountKey, SafeKey')
    expect(wrapper.text()).toContain('vw_powerbi_accounts.SafeKey')
    expect(wrapper.text()).toContain('dim_safe.SafeKey')
  })

  it('writes a GRANT SELECT for each object, for the account typed, quoting it as T-SQL does', async () => {
    const wrapper = await mountPage()
    expect(wrapper.find('.power-bi-script').text()).toContain('GRANT SELECT ON [dbo].[dim_safe] TO [DOMAIN\\PowerBI-Readers];')

    await wrapper.find('input').setValue('CORP\\BI]Team')
    const script = wrapper.find('.power-bi-script').text()
    expect(script).toContain('USE [BlueTrack];')
    expect(script).toContain('GRANT SELECT ON [dbo].[vw_powerbi_accounts] TO [CORP\\BI]]Team];')
  })

  // D-321: once the reader role is set up, adding the account to it is all the script needs to do.
  it('adds the account to the reader role when the role is set up', async () => {
    const wrapper = await mountPage({ ok: true, status: 200, json: () => Promise.resolve({ ...ACCESS, readerRoleExists: true }) })
    await wrapper.find('input').setValue('CORP\\BI Readers')

    const script = wrapper.find('.power-bi-script').text()
    expect(script).toContain('ALTER ROLE BlueTrack_PowerBI_Reader ADD MEMBER [CORP\\BI Readers];')
    expect(script).not.toContain('GRANT SELECT')
    expect(wrapper.text()).toContain('The BlueTrack_PowerBI_Reader role is set up')
  })

  it('says when the details could not be loaded', async () => {
    const wrapper = await mountPage({ ok: false, status: 403, json: () => Promise.resolve(null) })

    expect(wrapper.find('[role="alert"]').text()).toContain('Request failed: 403')
  })
})
