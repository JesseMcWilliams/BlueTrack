import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import { createPinia, setActivePinia } from 'pinia'
import DnaSection from './DnaSection.vue'
vi.mock('../../composables/useConfirmDialog', () => ({ confirmDelete: vi.fn().mockResolvedValue(true) }))
import { confirmDelete } from '../../composables/useConfirmDialog'

// D-325: uploading a DNA workbook on Data Sources, and what the latest one held.
function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const LATEST = { fileName: 'dna.xlsx', importedAt: '2026-11-04T10:00:00', importedByName: 'Admin', accountCount: 9, trackedCount: 2, sheetCounts: 'NC-DA=4; NC-LA=5' }

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/', component: { template: '<div />' } }, { path: '/reports/dna-accounts', name: 'reports-dna-accounts', component: { template: '<div />' } }]
  })
}

async function mountSection(handler) {
  globalThis.fetch = vi.fn(handler)
  const wrapper = mount(DnaSection, { global: { plugins: [makeRouter()] } })
  await flushPromises()
  return wrapper
}

async function chooseAndImport(wrapper) {
  const input = wrapper.find('input[type="file"]')
  Object.defineProperty(input.element, 'files', { value: [new File(['x'], 'dna.xlsx')] })
  await input.trigger('change')
  await wrapper.find('form').trigger('submit')
  await flushPromises()
}

describe('DnaSection.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    confirmDelete.mockResolvedValue(true)
  })
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows the latest import, and uploads a workbook', async () => {
    const wrapper = await mountSection((url) => Promise.resolve(url === '/api/admin/dna/import'
      ? jsonResponse({ latest: { ...LATEST, accountCount: 12 } })
      : jsonResponse({ latest: LATEST })))
    expect(wrapper.text()).toContain('9 accounts, 2 of them tracked in BlueTrack')
    expect(wrapper.text()).toContain('NC-DA=4; NC-LA=5')

    await chooseAndImport(wrapper)

    const post = globalThis.fetch.mock.calls.find(([url]) => url === '/api/admin/dna/import')
    expect(post[1].body.get('file').name).toBe('dna.xlsx')
    expect(wrapper.text()).toContain('12 accounts')
    expect(wrapper.text()).toContain('Imported.')
  })

  it('says why a workbook was refused', async () => {
    const wrapper = await mountSection((url) => Promise.resolve(url === '/api/admin/dna/import'
      ? jsonResponse({ detail: 'The workbook has no account sheets.' }, false, 400)
      : jsonResponse({ latest: null })))
    expect(wrapper.text()).toContain('No DNA workbook has been imported yet.')

    await chooseAndImport(wrapper)

    expect(wrapper.find('[role="alert"]').text()).toBe('The workbook has no account sheets.')
  })

  // D-343, D-344: Purge, and the DNA settings.
  describe('purge and settings', () => {
    const SETTINGS = { latest: LATEST, settings: { retentionDays: 90, restrictedColumns: ['OsVersion'] }, restrictableColumns: ['MachineName', 'OsVersion', 'LastLoginDate'] }
    const handler = (url, init) => {
      if (url === '/api/admin/dna') return Promise.resolve(jsonResponse(SETTINGS))
      if (url === '/api/admin/dna/track-rules') return Promise.resolve(jsonResponse({ fields: [], rules: [] }))
      if (url === '/api/admin/dna/purge') return Promise.resolve(jsonResponse({ deleted: 9 }))
      if (url === '/api/admin/dna/settings') return Promise.resolve(jsonResponse(JSON.parse(init.body)))
      return Promise.resolve(jsonResponse(null, false, 404))
    }

    it('purges after asking, and says how many accounts went', async () => {
      const wrapper = await mountSection(handler)
      await wrapper.find('button.purge').trigger('click')
      await flushPromises()
      expect(globalThis.fetch.mock.calls.some(([url, init]) => url === '/api/admin/dna/purge' && init?.method === 'POST')).toBe(true)
      expect(wrapper.text()).toContain('Purged 9 DNA accounts.')
    })

    it('shows the saved settings, the columns in alphabetical order, and saves a change', async () => {
      const wrapper = await mountSection(handler)
      const settings = wrapper.find('form.dna-settings')
      expect(settings.find('input[type="number"]').element.value).toBe('90')
      const labels = settings.findAll('.restricted-columns label')
      expect(labels.map(l => l.text())).toEqual(['Last login', 'Machine', 'OS version'])
      expect(labels[2].find('input').element.checked).toBe(true)

      await labels[1].find('input').setValue(true)
      await settings.find('input[type="number"]').setValue('')
      await settings.trigger('submit')
      await flushPromises()
      const put = globalThis.fetch.mock.calls.find(([url]) => url === '/api/admin/dna/settings')
      expect(JSON.parse(put[1].body)).toEqual({ retentionDays: null, restrictedColumns: ['OsVersion', 'MachineName'] })
      expect(wrapper.text()).toContain('DNA settings saved.')
    })
  })
})
