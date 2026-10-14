import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import AuditLogViewer from './AuditLogViewer.vue'
import ReportExportButtons from '../../components/ReportExportButtons.vue'

// D-124 Phase 3: this page had no component-level Vitest coverage before
// this phase -- establishes coverage for the new pager UI and the
// two-count summary, following Targets.test.js/AccessGroups.test.js's own
// D-121 mocking style.
function jsonResponse(body, { ok = true, status = 200, totalCount = null, filteredCount = totalCount } = {}) {
  return {
    ok,
    status,
    headers: {
      get: (name) => {
        if (name === 'X-Total-Count') return totalCount !== null ? String(totalCount) : null
        if (name === 'X-Filtered-Count') return filteredCount !== null ? String(filteredCount) : null
        return null
      }
    },
    json: () => Promise.resolve(body)
  }
}

const sampleEvent = {
  auditEventKey: 1,
  eventTypeName: 'FieldEdit',
  occurredAt: '2026-09-09T12:00:00Z',
  performedByName: 'Jane Analyst',
  entityName: 'fact_account_progress',
  entityKey: '42',
  detail: 'Stage changed',
  reason: null
}

function mockInitialLoad({ events = [sampleEvent], totalCount = events.length, filteredCount = totalCount } = {}) {
  globalThis.fetch = vi.fn((url) => {
    if (url.startsWith('/api/audit-log')) return Promise.resolve(jsonResponse(events, { totalCount, filteredCount }))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

describe('AuditLogViewer.vue', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads and renders the audit log', async () => {
    mockInitialLoad()
    const wrapper = mount(AuditLogViewer)
    await flushPromises()

    expect(wrapper.text()).toContain('FieldEdit')
  })

  it('shows the "Showing N of M matching (of total)" count summary once both headers are known', async () => {
    mockInitialLoad({ events: [sampleEvent], totalCount: 900, filteredCount: 55 })
    const wrapper = mount(AuditLogViewer)
    await flushPromises()

    expect(wrapper.text()).toContain('Showing 1–1 of 55 matching (900 total)')
  })

  it('sends page/pageSize query params on load, defaulting pageSize to 50', async () => {
    mockInitialLoad({ events: [] })
    const wrapper = mount(AuditLogViewer)
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/audit-log?'))
    expect(calls.some(u => u.includes('page=1') && u.includes('pageSize=50'))).toBe(true)
  })

  it('submitting the filter form resets to page 1, after Next advanced to page 2', async () => {
    mockInitialLoad({ events: [sampleEvent], totalCount: 900, filteredCount: 900 })
    const wrapper = mount(AuditLogViewer)
    await flushPromises()

    const nextButton = wrapper.findAll('.pager button').find(b => b.text().includes('Next'))
    await nextButton.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Page 2 of')

    await wrapper.get('input[placeholder="e.g. FieldEdit"]').setValue('FieldEdit')
    await wrapper.get('form.filter-row').trigger('submit')
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/audit-log?'))
    expect(calls.at(-1)).toContain('page=1')
    expect(calls.at(-1)).toContain('eventType=FieldEdit')
  })

  it('changing the page size persists the preference and re-fetches with the new pageSize', async () => {
    mockInitialLoad({ events: [sampleEvent], totalCount: 900, filteredCount: 900 })
    const wrapper = mount(AuditLogViewer)
    await flushPromises()

    const pageSizeSelect = wrapper.find('.pager select')
    await pageSizeSelect.setValue('250')
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenCalledWith('/api/me/preferences/PageSize', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ value: '250' })
    })
    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/audit-log?'))
    expect(calls.at(-1)).toContain('pageSize=250')
  })

  // D-231: Export CSV / Export Excel send the filters last applied with the
  // Filter button (and the current sort), never unsubmitted form edits.
  it('the export buttons get the applied filters and sort, not unsubmitted edits', async () => {
    mockInitialLoad()
    const wrapper = mount(AuditLogViewer)
    await flushPromises()

    const exportButtons = wrapper.getComponent(ReportExportButtons)
    expect(exportButtons.props('path')).toBe('/api/audit-log/export')
    expect(exportButtons.props('params')).toEqual({ eventType: '', entityName: '', fromDate: '', toDate: '', sort: '' })

    await wrapper.get('input[placeholder="e.g. FieldEdit"]').setValue('FieldEdit')
    await wrapper.get('input[placeholder="e.g. risk_exception"]').setValue('risk_exception')
    await wrapper.get('form.filter-row').trigger('submit')
    await flushPromises()
    await wrapper.findAll('th button').find(b => b.text().startsWith('Occurred At')).trigger('click')
    await flushPromises()
    // Typed but not submitted: neither the grid nor the export use it.
    await wrapper.get('input[placeholder="e.g. FieldEdit"]').setValue('Logon')

    expect(wrapper.getComponent(ReportExportButtons).props('params')).toEqual({
      eventType: 'FieldEdit', entityName: 'risk_exception', fromDate: '', toDate: '', sort: 'occurredAt:asc'
    })
  })

  it('Export CSV asks the API for every matching event with the applied filters', async () => {
    URL.createObjectURL = vi.fn(() => 'blob:audit')
    URL.revokeObjectURL = vi.fn()
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    mockInitialLoad()
    const wrapper = mount(AuditLogViewer)
    await flushPromises()

    await wrapper.get('input[placeholder="e.g. FieldEdit"]').setValue('FieldEdit')
    await wrapper.get('form.filter-row').trigger('submit')
    await flushPromises()
    await wrapper.get('input[placeholder="e.g. FieldEdit"]').setValue('Unsubmitted')

    globalThis.fetch.mockImplementation(() => Promise.resolve({
      ok: true,
      status: 200,
      headers: new Headers({ 'Content-Disposition': 'attachment; filename=BlueTrack_AuditLog_20261014-0930.csv' }),
      blob: () => Promise.resolve(new Blob(['Occurred At (UTC)']))
    }))
    await wrapper.findAll('button').find(b => b.text() === 'Export CSV').trigger('click')
    await flushPromises()

    expect(globalThis.fetch).toHaveBeenLastCalledWith('/api/audit-log/export?format=csv&eventType=FieldEdit')
    expect(wrapper.text()).toContain('Downloaded BlueTrack_AuditLog_20261014-0930.csv.')
  })

  it('paging after an unsubmitted edit keeps the applied filters', async () => {
    mockInitialLoad({ events: [sampleEvent], totalCount: 900, filteredCount: 900 })
    const wrapper = mount(AuditLogViewer)
    await flushPromises()

    await wrapper.get('input[placeholder="e.g. FieldEdit"]').setValue('Typed')
    await wrapper.findAll('.pager button').find(b => b.text().includes('Next')).trigger('click')
    await flushPromises()

    const calls = globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/audit-log?'))
    expect(calls.at(-1)).toContain('page=2')
    expect(calls.at(-1)).not.toContain('eventType')
  })
})
