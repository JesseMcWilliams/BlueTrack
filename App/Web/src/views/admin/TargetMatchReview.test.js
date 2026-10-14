import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import TargetMatchReview from './TargetMatchReview.vue'

// D-229: Target Match Review shows the imported row's own data and resolves
// a row as Linked (to the candidate, or a target picked in the typeahead),
// NewTarget or Ignored; a row queued before D-229 asks for a name.
function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const imported = {
  targetMatchReviewKey: 7,
  candidateTargetKey: 3,
  candidateTargetName: 'srv-primary',
  identifierType: 'IPAddress',
  identifierValue: '10.1.2.3',
  sourceFileName: 'cmdb.csv',
  targetName: 'db01',
  targetTypeDisplayName: 'Database',
  riskScore: 300,
  discoverySource: 'CMDB',
  identifiers: [
    { identifierType: 'Hostname', identifierValue: 'db01' },
    { identifierType: 'IPAddress', identifierValue: '10.1.2.3' }
  ]
}

const legacy = {
  targetMatchReviewKey: 8,
  candidateTargetKey: 4,
  candidateTargetName: 'srv-old',
  identifierType: 'IPAddress',
  identifierValue: '10.9.9.9',
  sourceFileName: null,
  targetName: null,
  identifiers: []
}

function mockFetch(rows) {
  globalThis.fetch = vi.fn((url, init) => {
    if (init?.method === 'POST') return Promise.resolve(jsonResponse(null, true, 204))
    if (url.startsWith('/api/admin/targets/search')) return Promise.resolve(jsonResponse([]))
    return Promise.resolve(jsonResponse(rows))
  })
}

function postBody() {
  const post = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'POST')
  return { url: post[0], body: JSON.parse(post[1].body) }
}

describe('TargetMatchReview.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows the imported row: name, type, source, risk and its other identifiers', async () => {
    mockFetch([imported])
    const wrapper = mount(TargetMatchReview)
    await flushPromises()

    const row = wrapper.get('tbody tr')
    expect(row.text()).toContain('db01 (Database)')
    expect(row.text()).toContain('Source: CMDB, Risk Score: 300')
    expect(row.text()).toContain('Hostname: db01')
    expect(row.text()).not.toContain('IPAddress: 10.1.2.3')
    expect(row.text()).toContain('srv-primary')
    expect(wrapper.html()).toContain('Link IPAddress 10.1.2.3 to target')
    expect(wrapper.findAll('tbody button').map(b => b.text())).toEqual(['Link', 'New Target', 'Ignore'])
  })

  it('Link sends Linked with the candidate when no other target is picked, then reloads', async () => {
    mockFetch([imported])
    const wrapper = mount(TargetMatchReview)
    await flushPromises()

    await wrapper.findAll('tbody button').find(b => b.text() === 'Link').trigger('click')
    await flushPromises()

    const { url, body } = postBody()
    expect(url).toBe('/api/admin/risk-scoring/target-match-review/7/resolve')
    expect(body).toEqual({ resolution: 'Linked', primaryTargetKey: 3 })
    expect(globalThis.fetch.mock.calls.filter(([u, init]) => u === '/api/admin/risk-scoring/target-match-review' && !init)).toHaveLength(2)
  })

  it('New Target and Ignore send their resolutions without a primary', async () => {
    mockFetch([imported])
    const wrapper = mount(TargetMatchReview)
    await flushPromises()

    await wrapper.findAll('tbody button').find(b => b.text() === 'Ignore').trigger('click')
    await flushPromises()
    expect(postBody().body).toEqual({ resolution: 'Ignored' })

    globalThis.fetch.mockClear()
    await wrapper.findAll('tbody button').find(b => b.text() === 'New Target').trigger('click')
    await flushPromises()
    expect(postBody().body).toEqual({ resolution: 'NewTarget' })
  })

  it('asks for a name on a row queued before its data was stored, and sends it with Link', async () => {
    mockFetch([legacy])
    const wrapper = mount(TargetMatchReview)
    await flushPromises()

    const name = wrapper.get('input[aria-label="Name of the target for IPAddress 10.9.9.9"]')
    expect(name.attributes('placeholder')).toBe('10.9.9.9')
    expect(wrapper.text()).toContain('Not stored (queued before this version). Name:')
    await name.setValue('  legacy-db  ')
    await wrapper.findAll('tbody button').find(b => b.text() === 'Link').trigger('click')
    await flushPromises()

    expect(postBody().body).toEqual({ resolution: 'Linked', primaryTargetKey: 4, newTargetName: 'legacy-db' })
  })

  it('shows the server\'s reason when a resolution is refused', async () => {
    globalThis.fetch = vi.fn((url, init) => Promise.resolve(init?.method === 'POST'
      ? jsonResponse({ detail: 'Target 99 doesn\'t exist -- choose another.' }, false, 400)
      : jsonResponse([imported])))
    const wrapper = mount(TargetMatchReview)
    await flushPromises()

    await wrapper.findAll('tbody button').find(b => b.text() === 'Link').trigger('click')
    await flushPromises()

    expect(wrapper.get('[role="alert"]').text()).toContain("doesn't exist")
  })

  it('says so when nothing is waiting', async () => {
    mockFetch([])
    const wrapper = mount(TargetMatchReview)
    await flushPromises()

    expect(wrapper.text()).toContain('No pending reviews.')
  })
})
