import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import ReportExportButtons from './ReportExportButtons.vue'

// D-220: the shared Export CSV / Export Excel buttons on every report page.
function fileResponse(fileName) {
  return {
    ok: true,
    status: 200,
    headers: new Headers({ 'Content-Disposition': `attachment; filename=${fileName}; filename*=UTF-8''${fileName}` }),
    blob: () => Promise.resolve(new Blob(['Account\r\n']))
  }
}

describe('ReportExportButtons.vue', () => {
  let clicked

  beforeEach(() => {
    clicked = []
    URL.createObjectURL = vi.fn(() => 'blob:report')
    URL.revokeObjectURL = vi.fn()
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function () {
      clicked.push({ href: this.href, download: this.download })
    })
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('asks the API for the format with the page\'s filters and sort, leaving out empty ones', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(fileResponse('BlueTrack_RiskScore_20261013-0930.csv')))
    const wrapper = mount(ReportExportButtons, {
      props: { path: '/api/reports/risk-score/export', params: { sort: 'accountName:desc', notDeletedOnly: false, empty: '', none: null } }
    })

    await wrapper.get('button:first-of-type').trigger('click')
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/reports/risk-score/export?format=csv&sort=accountName%3Adesc')
  })

  it('Export Excel asks for xlsx', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(fileResponse('BlueTrack_KpiSummary_20261013-0930.xlsx')))
    const wrapper = mount(ReportExportButtons, { props: { path: '/api/reports/kpi-summary/export', params: { notDeletedOnly: true } } })

    const excel = wrapper.findAll('button').find(b => b.text() === 'Export Excel')
    await excel.trigger('click')
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/reports/kpi-summary/export?format=xlsx&notDeletedOnly=true')
  })

  it('saves the file under the name the API gives', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(fileResponse('BlueTrack_OverdueAtRisk_20261013-0930.csv')))
    const wrapper = mount(ReportExportButtons, { props: { path: '/api/reports/overdue-at-risk/export' } })

    await wrapper.get('button').trigger('click')
    await flushPromises()

    expect(clicked).toEqual([{ href: 'blob:report', download: 'BlueTrack_OverdueAtRisk_20261013-0930.csv' }])
    expect(wrapper.get('[role="status"]').text()).toBe('Downloaded BlueTrack_OverdueAtRisk_20261013-0930.csv.')
  })

  it('disables both buttons while a download is in progress', async () => {
    let finish
    globalThis.fetch = vi.fn(() => new Promise(resolve => { finish = resolve }))
    const wrapper = mount(ReportExportButtons, { props: { path: '/api/reports/overdue-at-risk/export' } })

    await wrapper.get('button').trigger('click')
    const buttons = wrapper.findAll('button')
    expect(buttons.map(b => b.attributes('disabled'))).toEqual(['', ''])
    expect(buttons[0].attributes('aria-busy')).toBe('true')
    expect(wrapper.get('[role="status"]').text()).toBe('Preparing the export...')

    finish(fileResponse('BlueTrack_OverdueAtRisk_20261013-0930.csv'))
    await flushPromises()
    expect(wrapper.findAll('button').map(b => b.attributes('disabled'))).toEqual([undefined, undefined])
  })

  it("shows the API's own message when the export is refused", async () => {
    const detail = '120,000 rows match this report, more than the export limit of 100,000 rows. Narrow the report\'s filters and export again.'
    globalThis.fetch = vi.fn(() => Promise.resolve({
      ok: false,
      status: 400,
      json: () => Promise.resolve({ title: 'Too many rows to export', detail })
    }))
    const wrapper = mount(ReportExportButtons, { props: { path: '/api/reports/risk-score/export' } })

    await wrapper.get('button').trigger('click')
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toBe(`Could not export: ${detail}`)
    expect(clicked).toEqual([])
  })

  it('falls back to the status when the refusal has no message', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve({ ok: false, status: 403, json: () => Promise.reject(new Error('no body')) }))
    const wrapper = mount(ReportExportButtons, { props: { path: '/api/reports/risk-score/export' } })

    await wrapper.get('button').trigger('click')
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toBe('Could not export: Export failed: 403')
  })

  it('groups the buttons under an accessible name', () => {
    const wrapper = mount(ReportExportButtons, { props: { path: '/api/reports/kpi-summary/export' } })
    const group = wrapper.get('[role="group"]')

    expect(group.attributes('aria-label')).toBe('Export this report')
    expect(wrapper.findAll('button').map(b => b.text())).toEqual(['Export CSV', 'Export Excel'])
  })
})
