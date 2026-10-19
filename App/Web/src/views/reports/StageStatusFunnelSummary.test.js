import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import StageStatusFunnelSummary from './StageStatusFunnelSummary.vue'

// UI review W2-1, W7-1: each count opens the accounts behind it.
function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, headers: { get: () => null }, json: () => Promise.resolve(body) }
}

const rows = [
  { stageOrder: 1, stageName: 'Discovery', statusName: 'Not Started', accountCount: 30 },
  { stageOrder: 1, stageName: 'Discovery', statusName: 'Complete', accountCount: 12 },
  { stageOrder: 2, stageName: 'Planning', statusName: 'Not Started', accountCount: 0 },
  { stageOrder: 2, stageName: 'Planning', statusName: 'Complete', accountCount: 0 }
]

function makeRouter() {
  const page = { template: '<div />' }
  return createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/', name: 'home', component: page }, { path: '/accounts', name: 'account-progress-list', component: page }]
  })
}

async function mountReport(data = rows) {
  globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse(data)))
  const router = makeRouter()
  await router.push('/')
  const wrapper = mount(StageStatusFunnelSummary, { global: { plugins: [router] } })
  await flushPromises()
  return { wrapper, router }
}

describe('StageStatusFunnelSummary.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('links a cell to the list filtered to its stage and status', async () => {
    const { wrapper } = await mountReport()

    const cell = wrapper.findAll('a').find(a => a.attributes('aria-label') === 'Show the 30 accounts in Discovery with status Not Started')
    expect(cell.text()).toBe('30')
    expect(cell.attributes('href')).toBe('/accounts?stage=Discovery&status=Not+Started')
  })

  it('links a stage total to the list filtered to the stage', async () => {
    const { wrapper } = await mountReport()

    const total = wrapper.findAll('a').find(a => a.attributes('aria-label') === 'Show all 42 accounts in Discovery')
    expect(total.text()).toBe('42')
    expect(total.attributes('href')).toBe('/accounts?stage=Discovery')
  })

  it('leaves a zero as plain text', async () => {
    const { wrapper } = await mountReport()

    const planning = wrapper.findAll('tbody tr').find(r => r.text().startsWith('Planning'))
    expect(planning.findAll('td').map(td => td.text())).toEqual(['Planning', '0', '0', '0'])
    expect(planning.find('a').exists()).toBe(false)
  })

  it('following a cell lands on the list with both filters in the address', async () => {
    const { wrapper, router } = await mountReport()

    await wrapper.findAll('a').find(a => a.text() === '12').trigger('click')
    await flushPromises()

    expect(router.currentRoute.value.query).toEqual({ stage: 'Discovery', status: 'Complete' })
  })
})
