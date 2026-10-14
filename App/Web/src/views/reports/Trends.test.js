import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

// D-221: the test DOM has no canvas, so the vue-chartjs components are
// stubbed -- each records the data it was given, so the series can be checked.
vi.mock('vue-chartjs', async () => {
  const { defineComponent, h } = await import('vue')
  const stub = name => defineComponent({
    name,
    props: { data: Object, options: Object },
    setup: props => () => h('div', { class: `chart-stub chart-${name}`, 'data-series': props.data.datasets.map(d => d.label).join('|') })
  })
  return { Bar: stub('Bar'), Line: stub('Line') }
})

import Trends from './Trends.vue'

const report = {
  from: '2026-02-01',
  to: '2026-02-14',
  granularity: 'day',
  stages: [
    { order: 1, name: 'Discovered' }, { order: 2, name: 'Assessed / Prioritized' }, { order: 3, name: 'Onboarded to Vault' },
    { order: 4, name: 'Managed / Rotation Enabled' }, { order: 5, name: 'Verified / Compliant' }
  ],
  riskLevels: [{ order: 1, name: 'Low' }, { order: 2, name: 'Medium' }, { order: 3, name: 'High' }, { order: 4, name: 'Critical' }, { order: null, name: 'Not set' }],
  periods: [
    { periodStart: '2026-02-02', snapshotDate: '2026-02-02', totalAccounts: 1500, inScopeAccounts: 1200, compliantAccounts: 300, percentComplete: 25, stageCounts: [1000, 200, 100, 100, 100], riskCounts: [500, 400, 300, 200, 100] },
    { periodStart: '2026-02-03', snapshotDate: '2026-02-03', totalAccounts: 1500, inScopeAccounts: 0, compliantAccounts: 0, percentComplete: null, stageCounts: [1500, 0, 0, 0, 0], riskCounts: [0, 0, 0, 0, 1500] }
  ]
}

const options = {
  applications: [{ applicationKey: 7, applicationName: 'Payroll' }, { applicationKey: 8, applicationName: 'Billing' }],
  safes: [
    { safeKey: 11, safeName: 'PayrollSafe', sourceSystemName: 'Privilege Cloud', applicationKey: 7 },
    { safeKey: 12, safeName: 'BillingSafe', sourceSystemName: 'Privilege Cloud', applicationKey: 8 },
    { safeKey: 13, safeName: 'LooseSafe', sourceSystemName: 'Self-Hosted', applicationKey: null }
  ]
}

function mockApi(trends = report) {
  globalThis.fetch = vi.fn(url => {
    if (url === '/api/reports/trends/filter-options') return Promise.resolve({ ok: true, json: () => Promise.resolve(options) })
    if (url.startsWith('/api/reports/trends?')) return Promise.resolve({ ok: true, json: () => Promise.resolve(trends) })
    return Promise.resolve({ ok: false, status: 404, json: () => Promise.resolve({}) })
  })
}

const trendsCalls = () => globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/reports/trends?'))
const lastQuery = () => new URLSearchParams(trendsCalls().at(-1).split('?')[1])

describe('Trends.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('asks for the last 90 days by day, with no Application or Safe, by default', async () => {
    mockApi()
    mount(Trends)
    await flushPromises()

    const q = lastQuery()
    expect(q.get('granularity')).toBe('day')
    expect(q.has('applicationKey')).toBe(false)
    expect(q.has('safeKey')).toBe(false)
    const days = (new Date(q.get('to')) - new Date(q.get('from'))) / 86_400_000
    expect(days).toBe(89)
  })

  it('sends the chosen filters as query parameters, and narrows Safes to the chosen Application', async () => {
    mockApi()
    const wrapper = mount(Trends)
    await flushPromises()

    await wrapper.get('select[name="granularity"]').setValue('week')
    await wrapper.get('select[name="applicationKey"]').setValue('7')
    await flushPromises()
    const safeChoices = wrapper.get('select[name="safeKey"]').findAll('option').map(o => o.text())
    expect(safeChoices).toEqual(['All', 'PayrollSafe (Privilege Cloud)'])

    await wrapper.get('select[name="safeKey"]').setValue('11')
    await wrapper.get('input[name="from"]').setValue('2026-01-01')
    await flushPromises()

    const q = lastQuery()
    expect(q.get('granularity')).toBe('week')
    expect(q.get('applicationKey')).toBe('7')
    expect(q.get('safeKey')).toBe('11')
    expect(q.get('from')).toBe('2026-01-01')
  })

  it('clears a Safe that is not in the newly chosen Application', async () => {
    mockApi()
    const wrapper = mount(Trends)
    await flushPromises()

    await wrapper.get('select[name="safeKey"]').setValue('12')
    await wrapper.get('select[name="applicationKey"]').setValue('7')
    await flushPromises()

    expect(lastQuery().has('safeKey')).toBe(false)
  })

  it('draws the three charts, each with a title and its series', async () => {
    mockApi()
    const wrapper = mount(Trends)
    await flushPromises()

    expect(wrapper.findAll('h3').map(h => h.text())).toEqual(['Accounts by stage', '% complete', 'Accounts by risk level'])
    const bars = wrapper.findAll('.chart-Bar')
    expect(bars[0].attributes('data-series')).toBe('Discovered|Assessed / Prioritized|Onboarded to Vault|Managed / Rotation Enabled|Verified / Compliant')
    expect(bars[1].attributes('data-series')).toBe('Low|Medium|High|Critical|Not set')
    expect(wrapper.get('.chart-Line').attributes('data-series')).toBe('% complete')
    expect(wrapper.findAll('[role="img"]').length).toBe(3)
  })

  it('has a data table per chart for screen readers, shown on screen by its toggle', async () => {
    mockApi()
    const wrapper = mount(Trends)
    await flushPromises()

    const table = wrapper.get('#trends-stages-table')
    expect(table.classes()).toContain('visually-hidden')
    expect(table.text()).toContain(new Intl.NumberFormat().format(1000))

    const toggle = wrapper.findAll('button').find(b => b.attributes('aria-controls') === 'trends-stages-table')
    expect(toggle.attributes('aria-expanded')).toBe('false')
    await toggle.trigger('click')
    expect(toggle.attributes('aria-expanded')).toBe('true')
    expect(toggle.text()).toBe('Hide data table')
    expect(wrapper.get('#trends-stages-table').classes()).not.toContain('visually-hidden')

    // % complete: N/A with nothing in scope, never 0% or NaN.
    const percentRows = wrapper.get('#trends-percent-table').findAll('tbody tr')
    expect(percentRows[0].text()).toContain(new Intl.NumberFormat(undefined, { style: 'percent', maximumFractionDigits: 1 }).format(0.25))
    expect(percentRows[1].text()).toContain('N/A')
  })

  it('shows an empty-state message when there is no history in the range', async () => {
    mockApi({ ...report, periods: [] })
    const wrapper = mount(Trends)
    await flushPromises()

    expect(wrapper.get('.trends-empty').text()).toContain('No progress history in this range')
    expect(wrapper.find('.chart-stub').exists()).toBe(false)
  })

  it("shows the API's message when the request is refused", async () => {
    globalThis.fetch = vi.fn(url => url.startsWith('/api/reports/trends?')
      ? Promise.resolve({ ok: false, status: 400, json: () => Promise.resolve({ detail: 'from must be on or before to.' }) })
      : Promise.resolve({ ok: true, json: () => Promise.resolve(options) }))
    const wrapper = mount(Trends)
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toContain('from must be on or before to.')
  })
})
