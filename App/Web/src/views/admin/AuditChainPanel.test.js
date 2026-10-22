import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import AuditChainPanel from './AuditChainPanel.vue'
import { formatUtcDateTime } from '../../utils/formatLocale'

// D-295: the audit log's seals on the Audit Log page.
function json(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const status = { lastChainSeq: 1234, lastSealedAt: '2026-10-21T20:00:00', unsealed: 2, lastVerifiedAt: null, lastVerifyIntact: null, lastVerifySummary: null }

function mockApi(verifyResult) {
  globalThis.fetch = vi.fn((url, init) => {
    if (url === '/api/audit-log/chain/verify' && init?.method === 'POST') return Promise.resolve(json(verifyResult))
    return Promise.resolve(json(status))
  })
}

describe('AuditChainPanel.vue', () => {
  afterEach(() => vi.restoreAllMocks())

  it('shows how far sealing has got', async () => {
    mockApi(null)
    const wrapper = mount(AuditChainPanel)
    await flushPromises()
    expect(wrapper.text()).toContain('1,234 events sealed.')
    expect(wrapper.text()).toContain(`Last sealed ${formatUtcDateTime('2026-10-21T20:00:00')}.`)
    expect(wrapper.text()).toContain('2 waiting to be sealed.')
  })

  it('Verify says the chain is intact', async () => {
    mockApi({ intact: true, checked: 1234, problems: [], moreProblems: false })
    const wrapper = mount(AuditChainPanel)
    await flushPromises()
    await wrapper.findAll('button').find(b => b.text() === 'Verify').trigger('click')
    await flushPromises()
    expect(globalThis.fetch).toHaveBeenCalledWith('/api/audit-log/chain/verify', { method: 'POST' })
    expect(wrapper.text()).toContain('Intact: all 1,234 sealed events checked.')
  })

  it('Verify lists the problems when the chain is broken', async () => {
    mockApi({ intact: false, checked: 10, moreProblems: false, problems: [{ chainSeq: 7, auditEventKey: 99, kind: 'Edited' }, { chainSeq: 9, auditEventKey: null, kind: 'SealMissing' }] })
    const wrapper = mount(AuditChainPanel)
    await flushPromises()
    await wrapper.findAll('button').find(b => b.text() === 'Verify').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role="alert"]').text()).toContain('Broken')
    const rows = wrapper.findAll('tbody tr').map(r => r.findAll('td').map(td => td.text()))
    expect(rows).toEqual([['7', '99', 'The event or its seal was changed'], ['9', '', 'A seal was deleted']])
  })
})
