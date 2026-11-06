import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

vi.mock('../../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))

import { confirmDelete } from '../../composables/useConfirmDialog'
import ImportLoadSection from './ImportLoadSection.vue'
import { formatSeconds, durationText, procedureName } from './runProgress'

// D-337: the nightly job's Import, Load and Snapshot on Admin > Data Sources.

function jsonResponse(body, ok = true, status = 200) {
  return Promise.resolve({ ok, status, json: () => Promise.resolve(body) })
}

const run = (overrides = {}) => ({
  etlRunKey: 10, stepName: 'Load', startedAt: '2026-11-05T10:00:00', completedAt: '2026-11-05T10:02:30', status: 'Succeeded',
  errorMessage: null, errorProcedure: null, errorLine: null, startedBy: 'NT SERVICE\\SQLSERVERAGENT', stepCount: 20, ...overrides
})

const activity = (overrides = {}) => ({
  etlRunKey: 11, stepName: 'Load', startedAt: '2026-11-05T10:00:00', sessionId: 63, canSeeSessions: true, sessionFound: true,
  requestStatus: 'suspended', command: 'UPDATE', waitType: 'LCK_M_X', waitMs: 660000, waitResource: 'KEY: 8:1 (ab)', lastWaitType: 'LCK_M_X',
  requestElapsedMs: 700000, blockingSessionId: 71, currentProcedure: 'dbo.usp_Load_FactAccount', currentStatement: 'UPDATE fa SET ...',
  blockerLogin: 'COMPANY\\jdoe', blockerHost: 'WS01', blockerProgram: 'Microsoft SQL Server Management Studio', blockerStatus: 'sleeping',
  blockerDatabase: 'BlueTrack', blockerOpenTransactions: 1, blockerLastRequestStart: '2026-11-05T09:50:00', blockerCommand: null, blockerWaitType: null,
  ...overrides
})

function routeFetch(handlers) {
  globalThis.fetch = vi.fn(url => {
    const handler = handlers[url]
    if (!handler) throw new Error(`Unexpected fetch: ${url}`)
    return typeof handler === 'function' ? handler() : handler
  })
}

describe('ImportLoadSection.vue', () => {
  beforeEach(() => {
    confirmDelete.mockResolvedValue(true)   // vi.restoreAllMocks resets it
  })

  afterEach(() => {
    vi.restoreAllMocks()
    delete globalThis.fetch
  })

  it('lists the latest runs with how long each took and why one failed', async () => {
    routeFetch({
      '/api/admin/etl-runs': jsonResponse({
        serverTime: '2026-11-05T11:00:00',
        runs: [run(), run({ etlRunKey: 9, stepName: 'Import', stepCount: 0, completedAt: '2026-11-05T10:00:05' }),
          run({ etlRunKey: 8, status: 'Failed', errorMessage: 'Deadlock', errorProcedure: 'dbo.usp_Load_FactAccount', errorLine: 42 })],
        running: []
      })
    })
    const wrapper = mount(ImportLoadSection)
    await flushPromises()

    const rows = wrapper.findAll('tbody tr').map(r => r.text())
    expect(rows[0]).toContain('Load')
    expect(rows[0]).toContain('2 min')
    expect(rows[0]).toContain('Procedures (20)')
    expect(rows[1]).toContain('Import')
    expect(rows[1]).not.toContain('Procedures')
    expect(rows[2]).toContain('Deadlock (in usp_Load_FactAccount, line 42)')
  })

  it('shows a Load\'s procedures on request', async () => {
    routeFetch({
      '/api/admin/etl-runs': jsonResponse({ serverTime: '2026-11-05T11:00:00', runs: [run()], running: [] }),
      '/api/admin/etl-runs/10/steps': jsonResponse([
        { stepOrder: 1, procName: 'dbo.usp_Load_DimLocation', startedAt: '2026-11-05T10:00:00', completedAt: '2026-11-05T10:00:01', status: 'RolledBack' },
        { stepOrder: 2, procName: 'dbo.usp_Load_DimPlatform', startedAt: '2026-11-05T10:00:01', completedAt: '2026-11-05T10:00:02', status: 'Failed' }
      ])
    })
    const wrapper = mount(ImportLoadSection)
    await flushPromises()
    await wrapper.find('tbody button').trigger('click')
    await flushPromises()

    const steps = wrapper.findAll('.run-steps tbody tr').map(r => r.text())
    expect(steps).toEqual(['1usp_Load_DimLocation1 sRolled back', '2usp_Load_DimPlatform1 sFailed'])
    expect(wrapper.find('tbody button').attributes('aria-expanded')).toBe('true')
  })

  it('for a run still going, names its procedure, its wait and the session blocking it', async () => {
    routeFetch({
      '/api/admin/etl-runs': jsonResponse({
        serverTime: '2026-11-05T10:15:00',
        runs: [run({ etlRunKey: 11, status: 'Started', completedAt: null, stepCount: 9 })],
        running: [{ activity: activity(), currentStep: { stepOrder: 9, procName: 'dbo.usp_Load_FactAccount', startedAt: '2026-11-05T10:03:00', completedAt: null, status: 'Started' }, stepsDone: 8 }]
      })
    })
    const wrapper = mount(ImportLoadSection)
    await flushPromises()

    const live = wrapper.find('.run-live').text()
    expect(live).toContain('Load running since')
    expect(live).toContain('(15 min)')
    expect(live).toContain('Now in procedure 9, usp_Load_FactAccount, for 12 min; 8 finished before it.')
    expect(live).toContain('SQL Server session 63: suspended UPDATE.')
    expect(live).toContain('Waiting on LCK_M_X for 11 min.')
    expect(live).toContain('Blocked by session 71: COMPANY\\jdoe on WS01, Microsoft SQL Server Management Studio, database BlueTrack.')
    expect(live).toContain('That session is sleeping with 1 open transaction(s), running idle')
    wrapper.unmount()   // stops the polling timer
  })

  it('says when the run\'s session has ended, or when its waits can\'t be seen', async () => {
    routeFetch({
      '/api/admin/etl-runs': jsonResponse({
        serverTime: '2026-11-05T10:15:00',
        runs: [],
        running: [
          { activity: activity({ etlRunKey: 12, sessionFound: false, sessionId: 52 }), currentStep: null, stepsDone: 0 },
          { activity: activity({ etlRunKey: 13, canSeeSessions: false, sessionFound: false }), currentStep: null, stepsDone: 0 }
        ]
      })
    })
    const wrapper = mount(ImportLoadSection)
    await flushPromises()

    const live = wrapper.findAll('.run-live').map(r => r.text())
    expect(live[0]).toContain('Its SQL Server session (52) has ended, so this run will never finish')
    expect(live[1]).toContain("the installer's Db.SessionActivity step hasn't signed the procedure")
    wrapper.unmount()
  })

  it('checks back while a run is going, and stops once nothing is', async () => {
    vi.useFakeTimers()
    try {
      let running = [{ activity: activity(), currentStep: null, stepsDone: 0 }]
      routeFetch({ '/api/admin/etl-runs': () => jsonResponse({ serverTime: '2026-11-05T10:15:00', runs: [], running }) })
      const wrapper = mount(ImportLoadSection)
      await flushPromises()
      running = []
      await vi.advanceTimersByTimeAsync(5000)
      await flushPromises()
      const calls = globalThis.fetch.mock.calls.length
      await vi.advanceTimersByTimeAsync(20000)
      expect(globalThis.fetch.mock.calls.length).toBe(calls)
      expect(calls).toBe(2)
      wrapper.unmount()
    } finally {
      vi.useRealTimers()
    }
  })

  it('formats lengths of time and procedure names', () => {
    expect([formatSeconds(45), formatSeconds(720), formatSeconds(7500)]).toEqual(['45 s', '12 min', '2 h 5 min'])
    expect(durationText('2026-11-05T10:00:00', null, '2026-11-05T10:01:30')).toBe('1 min')
    expect(durationText(null, null, '2026-11-05T10:01:30')).toBe('')
    expect(procedureName('dbo.usp_Load_FactAccount')).toBe('usp_Load_FactAccount')
  })

  // ---- D-348 ----

  it('shows the stall time and saves a new one', async () => {
    const calls = []
    globalThis.fetch = vi.fn((url, options) => {
      calls.push({ url, options })
      if (url === '/api/admin/etl-runs') return jsonResponse({ serverTime: '2026-11-05T11:00:00', runs: [], running: [], stallMinutes: 45 })
      if (url === '/api/admin/etl-runs/settings') return jsonResponse({ stallMinutes: 60 })
      throw new Error(`Unexpected fetch: ${url}`)
    })
    const wrapper = mount(ImportLoadSection)
    await flushPromises()

    const input = wrapper.find('input[type="number"]')
    expect(input.element.value).toBe('45')
    await input.setValue(60)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    const put = calls.find(c => c.url === '/api/admin/etl-runs/settings')
    expect(put.options.method).toBe('PUT')
    expect(JSON.parse(put.options.body)).toEqual({ stallMinutes: 60 })
    expect(wrapper.text()).toContain('Saved.')
  })

  it('offers Cancel load only while a run is going, asks first, and shows why the Agent refused', async () => {
    const calls = []
    globalThis.fetch = vi.fn((url, options) => {
      calls.push({ url, options })
      if (url === '/api/admin/etl-runs') return jsonResponse({ serverTime: '2026-11-05T11:00:00', runs: [], running: [{ activity: activity(), currentStep: null, stepsDone: 0 }], stallMinutes: 30 })
      if (url === '/api/admin/etl-runs/cancel') return jsonResponse({ detail: 'The job is not currently running.' }, false, 409)
      throw new Error(`Unexpected fetch: ${url}`)
    })
    const wrapper = mount(ImportLoadSection)
    await flushPromises()

    const cancel = wrapper.findAll('button').find(b => b.text() === 'Cancel load')
    confirmDelete.mockResolvedValueOnce(false)
    await cancel.trigger('click')
    await flushPromises()
    expect(calls.some(c => c.url === '/api/admin/etl-runs/cancel')).toBe(false)

    await cancel.trigger('click')
    await flushPromises()
    expect(calls.find(c => c.url === '/api/admin/etl-runs/cancel').options.method).toBe('POST')
    expect(wrapper.find('[role="alert"]').text()).toContain('The job is not currently running.')
    wrapper.unmount()
  })

  it('has no Cancel load when nothing is running', async () => {
    routeFetch({ '/api/admin/etl-runs': jsonResponse({ serverTime: '2026-11-05T11:00:00', runs: [], running: [], stallMinutes: 30 }) })
    const wrapper = mount(ImportLoadSection)
    await flushPromises()
    expect(wrapper.findAll('button').some(b => b.text() === 'Cancel load')).toBe(false)
  })

  // ---- D-351 ----

  it('lists the files an Import imported and the pull it checked, and names the file a running Import is on', async () => {
    routeFetch({
      '/api/admin/etl-runs': jsonResponse({
        serverTime: '2026-11-05T11:00:00',
        runs: [run({ etlRunKey: 9, stepName: 'Import', stepCount: 0, fileCount: 2, completedAt: '2026-11-05T10:01:00' })],
        running: [{ activity: activity({ etlRunKey: 12, stepName: 'Import', waitType: null, blockingSessionId: null }), currentStep: null, currentFile: '/exports/Accounts.csv', stepsDone: 0 }],
        stallMinutes: 30
      }),
      '/api/admin/etl-runs/9/import': jsonResponse({
        pull: { pullKey: 14, completedAt: '2026-11-05T09:31:00', status: 'Succeeded' },
        files: [
          { sourceFileName: '/exports/Safes.csv', sourceSystemName: 'Privilege Cloud', rowsLoaded: 1200, startedAt: '2026-11-05T10:00:00', completedAt: '2026-11-05T10:00:05', status: 'Succeeded', errorMessage: null },
          { sourceFileName: '/exports/Accounts.csv', sourceSystemName: 'Privilege Cloud', rowsLoaded: null, startedAt: '2026-11-05T10:00:05', completedAt: '2026-11-05T10:00:06', status: 'Failed', errorMessage: 'Cannot bulk load' }
        ]
      })
    })
    const wrapper = mount(ImportLoadSection)
    await flushPromises()

    expect(wrapper.text()).toContain('Importing /exports/Accounts.csv.')
    const button = wrapper.findAll('tbody button').find(b => b.text() === 'Files (2)')
    await button.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Checked Self-Hosted Direct pull 14 (Succeeded')
    const rows = wrapper.findAll('.run-steps tbody tr').map(r => r.text())
    expect(rows[0]).toContain('Safes.csv')
    expect(rows[0]).toContain('1,200')
    expect(rows[1]).toContain('Cannot bulk load')
    wrapper.unmount()
  })
})
