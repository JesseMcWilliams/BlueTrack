import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'

vi.mock('vue-chartjs', async () => {
  const { defineComponent, h } = await import('vue')
  const stub = name => defineComponent({ name, props: { data: Object, options: Object }, setup: () => () => h('div', { class: `chart-stub chart-${name}` }) })
  return { Bar: stub('Bar'), Line: stub('Line') }
})

import Trends from './Trends.vue'

// UI review W7-2: the range, grouping, Application and Safe are in the page address.
const report = { from: '2026-02-01', to: '2026-02-14', granularity: 'day', stages: [], riskLevels: [], periods: [] }
const options = {
  applications: [{ applicationKey: 7, applicationName: 'Payroll' }, { applicationKey: 8, applicationName: 'Billing' }],
  safes: [
    { safeKey: 11, safeName: 'PayrollSafe', sourceSystemName: 'Privilege Cloud', applicationKey: 7 },
    { safeKey: 12, safeName: 'BillingSafe', sourceSystemName: 'Privilege Cloud', applicationKey: 8 }
  ]
}

function mockApi() {
  globalThis.fetch = vi.fn(url => {
    if (url === '/api/reports/trends/filter-options') return Promise.resolve({ ok: true, json: () => Promise.resolve(options) })
    if (url.startsWith('/api/reports/trends?')) return Promise.resolve({ ok: true, json: () => Promise.resolve(report) })
    return Promise.resolve({ ok: false, status: 404, json: () => Promise.resolve({}) })
  })
}

const calls = () => globalThis.fetch.mock.calls.map(c => c[0]).filter(u => u.startsWith('/api/reports/trends?'))
const lastQuery = () => new URLSearchParams(calls().at(-1).split('?')[1])

async function mountAt(path) {
  const page = { template: '<div />' }
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/', name: 'home', component: page }, { path: '/reports/trends', name: 'reports-trends', component: page }]
  })
  await router.push(path)
  const wrapper = mount(Trends, { global: { plugins: [router] } })
  await flushPromises()
  return { wrapper, router }
}

describe('Trends.vue address', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('reads the range, grouping, Application and Safe from the address, and shows them', async () => {
    mockApi()
    const { wrapper } = await mountAt('/reports/trends?from=2026-01-01&to=2026-02-01&granularity=week&application=7&safe=11')

    expect(Object.fromEntries(new URLSearchParams(calls()[0].split('?')[1]))).toEqual({
      from: '2026-01-01', to: '2026-02-01', granularity: 'week', applicationKey: '7', safeKey: '11'
    })
    expect(wrapper.get('input[name="from"]').element.value).toBe('2026-01-01')
    expect(wrapper.get('select[name="granularity"]').element.value).toBe('week')
    expect(wrapper.get('select[name="applicationKey"]').element.value).toBe('7')
    expect(wrapper.get('select[name="safeKey"]').element.value).toBe('11')   // not cleared before the choices had loaded
  })

  it('ignores values the page does not have: a bad date, an unknown grouping', async () => {
    mockApi()
    await mountAt('/reports/trends?from=junk&to=2026-13-45&granularity=hour')

    const q = lastQuery()
    expect(q.get('granularity')).toBe('day')
    expect(q.get('from')).toMatch(/^\d{4}-\d\d-\d\d$/)
    expect(q.get('from')).not.toBe('junk')
  })

  it('writes a filter change to the address, and the defaults are left out', async () => {
    mockApi()
    const { wrapper, router } = await mountAt('/reports/trends')
    expect(router.currentRoute.value.query).toEqual({})

    await wrapper.get('select[name="granularity"]').setValue('week')
    await wrapper.get('select[name="applicationKey"]').setValue('8')
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ granularity: 'week', application: '8' })

    await wrapper.get('select[name="granularity"]').setValue('day')
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ application: '8' })
  })

  it('follows the address when it changes under the page', async () => {
    mockApi()
    const { wrapper, router } = await mountAt('/reports/trends')
    const before = calls().length

    await router.push('/reports/trends?granularity=week&application=7')
    await flushPromises()

    expect(calls().length).toBeGreaterThan(before)
    expect(lastQuery().get('granularity')).toBe('week')
    expect(lastQuery().get('applicationKey')).toBe('7')
    expect(wrapper.get('select[name="granularity"]').element.value).toBe('week')
  })
})
