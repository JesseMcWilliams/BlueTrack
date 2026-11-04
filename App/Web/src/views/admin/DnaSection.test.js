import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import DnaSection from './DnaSection.vue'

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
})
