import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import KpiSummary from './reports/KpiSummary.vue'
import RiskScoreReport from './reports/RiskScoreReport.vue'
import DeploymentInfo from './admin/DeploymentInfo.vue'
import ApplicationEdit from './admin/ApplicationEdit.vue'

vi.mock('../composables/useConfirmDialog', () => ({ confirmDelete: vi.fn().mockResolvedValue(true) }))
import { confirmDelete } from '../composables/useConfirmDialog'

// UI review W7-3, W7-6, W7-7, W9-7, W8-4.
const stub = { template: '<div />' }
function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/a', name: 'account-progress-list', component: stub },
      { path: '/a/:accountKey', name: 'account-progress-detail', component: stub },
      { path: '/x', component: stub },
      { path: '/:p(.*)*', component: stub }
    ]
  })
}
function answer(body, extra = {}) {
  return { ok: true, status: 200, headers: { get: (n) => (n === 'X-Total-Count' || n === 'X-Filtered-Count' ? String(Array.isArray(body) ? body.length : 0) : null) }, json: () => Promise.resolve(body), ...extra }
}

describe('reports and operations fixes', () => {
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => vi.restoreAllMocks())

  it('KPI Summary says what each count means and when it was read (W7-3)', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(answer({ totalAccounts: 10, inScopeAccounts: 8, onboardedAccounts: 4, managedAccounts: 2, compliantAccounts: 1 })))
    const wrapper = mount(KpiSummary, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    expect(wrapper.find('h3').text()).toBe('What the counts mean')
    expect(wrapper.findAll('dt').map(d => d.text())).toEqual(['All Accounts', 'In Scope', 'Onboarded', 'Managed', 'Compliant'])
    expect(wrapper.text()).toContain('Counts read ')
  })

  it('Risk Score says when there are no accounts, and an opened account links to its detail (W7-6, W7-7)', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(answer([])))
    const empty = mount(RiskScoreReport, { global: { plugins: [makeRouter()] } })
    await flushPromises()
    expect(empty.find('.empty-list').text()).toBe('No accounts have a risk score yet.')

    const row = { accountKey: 7, accountName: 'svc-web01', computedRiskScore: 1, effectiveRiskScore: 1, isRiskScoreStale: false }
    globalThis.fetch = vi.fn((url) => Promise.resolve(url.includes('contributors') ? answer([]) : answer([row])))
    const wrapper = mount(RiskScoreReport, { global: { plugins: [makeRouter()] } })
    await flushPromises()
    const toggle = wrapper.find('button.link-button')
    expect(toggle.attributes('aria-expanded')).toBe('false')
    await toggle.trigger('click')
    await flushPromises()
    expect(wrapper.find('button.link-button').attributes('aria-expanded')).toBe('true')
    expect(wrapper.find('tbody a').attributes('href')).toBe('/a/7')
  })

  it('Backup App asks first and then names the downloaded file (W9-7)', async () => {
    URL.createObjectURL = vi.fn(() => 'blob:x')
    URL.revokeObjectURL = vi.fn()
    globalThis.fetch = vi.fn((url, init) => {
      if (init?.method === 'POST') {
        return Promise.resolve({ ok: true, status: 200, blob: () => Promise.resolve(new Blob(['x'])), headers: { get: () => 'attachment; filename="BlueTrack_ConfigBackup_1.zip"' } })
      }
      return Promise.resolve(answer(url.endsWith('/backups') ? [] : { environment: 'Development', version: '1', healthChecks: [], backupStatus: { available: true, entries: [] } }))
    })
    const wrapper = mount(DeploymentInfo, { global: { plugins: [makeRouter()] } })
    await flushPromises()
    const backup = () => wrapper.findAll('button').find(b => b.text() === 'Backup App')

    confirmDelete.mockResolvedValueOnce(false)
    await backup().trigger('click')
    await flushPromises()
    expect(globalThis.fetch.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)

    confirmDelete.mockResolvedValueOnce(true)
    await backup().trigger('click')
    await flushPromises()
    expect(confirmDelete).toHaveBeenLastCalledWith(expect.stringContaining('Back up the application now?'), 'Backup App')
    expect(wrapper.find('[role="status"]').text()).toBe('Backup complete. Downloaded BlueTrack_ConfigBackup_1.zip.')
  })

  it('marks a failed backup and a health check that is not Healthy (W9-4)', async () => {
    globalThis.fetch = vi.fn((url) => Promise.resolve(answer(url.endsWith('/backups')
      ? [{ backupRunKey: 1, startedAt: '2026-10-19T10:00:00', triggerType: 'Schedule', outcome: 'Failed', filePath: null, filesDeleted: 0, errorMessage: 'disk full' },
        { backupRunKey: 2, startedAt: '2026-10-18T10:00:00', triggerType: 'Schedule', outcome: 'Succeeded', filePath: 'x.bak', filesDeleted: 0, errorMessage: null }]
      : { environment: 'Development', version: '1', healthChecks: [{ name: 'SQL Server', status: 'Healthy', description: 'ok' }, { name: 'Secrets', status: 'Unhealthy', description: 'down' }], backupStatus: { available: true, entries: [] } })))
    const wrapper = mount(DeploymentInfo, { global: { plugins: [makeRouter()] } })
    await flushPromises()

    const bad = wrapper.findAll('td.status-bad').map(td => td.text().replace('⚠', '').trim())
    expect(bad).toEqual(['Unhealthy', 'Failed'])
  })

  it('Application email fields are email inputs (W8-4)', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(answer([])))
    const wrapper = mount(ApplicationEdit, { global: { plugins: [makeRouter()] } })
    await flushPromises()
    const types = wrapper.findAll('label').filter(l => /Email/.test(l.text())).map(l => l.find('input').attributes('type'))
    expect(types).toEqual(['email', 'email'])
  })
})
