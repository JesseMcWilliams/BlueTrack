import { describe, it, expect, afterEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import SiemForwarding from './SiemForwarding.vue'

// D-234: Admin > SIEM Forwarding.
function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, json: () => Promise.resolve(body) }
}

function page(overrides = {}) {
  return {
    settings: {
      isEnabled: false, host: 'siem.corp.example', port: 6514, transport: 'Tls', framing: 'OctetCounting', facility: 20,
      syslogHostName: null, appName: 'BlueTrack', timeoutSeconds: 10, maxMessageBytes: 8192, tlsTrustMode: 'System',
      tlsFingerprint: null, tlsCaCertificate: null, tlsCheckRevocation: true, ...overrides.settings
    },
    eventTypes: [
      { auditEventTypeKey: 1, eventTypeName: 'Logon', description: 'Successful application logon', isForwarded: true, cefSeverity: 3 },
      { auditEventTypeKey: 15, eventTypeName: 'BreakGlassLogon', description: 'A break-glass account signed in', isForwarded: true, cefSeverity: 8 }
    ],
    status: {
      watermark: 100, enabledAt: '2026-10-14T12:00:00', enabledFromAuditEventKey: 500, lastSuccessAt: '2026-10-14T12:05:03.123',
      eventsSent: 1234, lastError: null, lastErrorAt: null, consecutiveFailures: 0, pendingCount: 2, oldestPendingAt: '2026-10-14T12:06:00',
      ...overrides.status
    },
    defaultHostName: 'bt01.corp.example',
    deviceVersion: '1.0.0-dev.3+abc1234'
  }
}

function mockApi(body = page(), overrides = {}) {
  globalThis.fetch = vi.fn((url, options = {}) => {
    const key = `${options.method ?? 'GET'} ${url}`
    if (overrides[key]) return Promise.resolve(overrides[key])
    if (key === 'GET /api/admin/siem-forwarding') return Promise.resolve(jsonResponse(body))
    if (key === 'PUT /api/admin/siem-forwarding') return Promise.resolve({ ok: true, status: 204, json: () => Promise.resolve(null) })
    if (key === 'POST /api/admin/siem-forwarding/test') return Promise.resolve(jsonResponse({ success: true, target: 'siem.corp.example:6514 (Tls, OctetCounting)' }))
    return Promise.resolve(jsonResponse(null, { ok: false, status: 404 }))
  })
}

function lastBody(method) {
  const call = globalThis.fetch.mock.calls.filter(([, options]) => options?.method === method).at(-1)
  return JSON.parse(call[1].body)
}

describe('SiemForwarding.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows the status, the settings and every event type', async () => {
    mockApi()
    const wrapper = mount(SiemForwarding)
    await flushPromises()

    expect(wrapper.get('h2').text()).toBe('SIEM Forwarding')
    expect(wrapper.text()).toContain('2026-10-14 12:05:03 UTC')
    expect(wrapper.text()).toContain('audit event 500')
    expect(wrapper.text()).toContain('2 (oldest from 2026-10-14 12:06:00 UTC)')
    expect(wrapper.get('input[type="text"]').element.value).toBe('siem.corp.example')
    expect(wrapper.find('input[placeholder="bt01.corp.example"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('BreakGlassLogon')
    expect(wrapper.get('input[aria-label="Severity of BreakGlassLogon"]').element.value).toBe('8')
    expect(wrapper.text()).toContain('1.0.0-dev.3+abc1234')
  })

  it('shows the last error when the last send failed', async () => {
    mockApi(page({ status: { lastError: 'Connection refused', lastErrorAt: '2026-10-14T12:07:00', consecutiveFailures: 3 } }))
    const wrapper = mount(SiemForwarding)
    await flushPromises()
    expect(wrapper.text()).toContain('2026-10-14 12:07:00 UTC, 3 in a row: Connection refused')
  })

  it('shows the TLS options only for TLS, and the fingerprint or CA field for its mode', async () => {
    mockApi(page({ settings: { tlsTrustMode: 'Fingerprint' } }))
    const wrapper = mount(SiemForwarding)
    await flushPromises()
    expect(wrapper.text()).toContain('Certificate fingerprint')
    expect(wrapper.find('textarea').exists()).toBe(false)

    const selects = wrapper.findAll('select')
    await selects[2].setValue('CaCertificate')
    expect(wrapper.find('textarea').exists()).toBe(true)

    await selects[0].setValue('Tcp')
    expect(wrapper.text()).not.toContain('Trust the collector')
    expect(wrapper.get('input[type="number"]').element.value).toBe('514')   // default port follows the transport
  })

  it('saves the settings and each event type', async () => {
    mockApi()
    const wrapper = mount(SiemForwarding)
    await flushPromises()

    await wrapper.get('input[type="checkbox"]').setValue(true)
    await wrapper.get('input[aria-label="Forward Logon"]').setValue(false)
    await wrapper.get('input[aria-label="Severity of BreakGlassLogon"]').setValue('9')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const body = lastBody('PUT')
    expect(body.isEnabled).toBe(true)
    expect(body.port).toBe(6514)
    expect(body.eventTypes).toEqual([
      { auditEventTypeKey: 1, isForwarded: false, cefSeverity: 3 },
      { auditEventTypeKey: 15, isForwarded: true, cefSeverity: 9 }
    ])
    expect(wrapper.text()).toContain('Settings saved.')
  })

  it('Forward None clears every type', async () => {
    mockApi()
    const wrapper = mount(SiemForwarding)
    await flushPromises()
    await wrapper.findAll('button').find(b => b.text() === 'Forward None').trigger('click')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(lastBody('PUT').eventTypes.every(t => t.isForwarded === false)).toBe(true)
  })

  it('shows the server\'s validation message', async () => {
    mockApi(page(), {
      'PUT /api/admin/siem-forwarding': jsonResponse({ detail: 'Port must be 1-65535.' }, { ok: false, status: 400 })
    })
    const wrapper = mount(SiemForwarding)
    await flushPromises()
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toBe('Port must be 1-65535.')
  })

  it('reports the Test result, success or failure', async () => {
    mockApi()
    const wrapper = mount(SiemForwarding)
    await flushPromises()
    const testButton = () => wrapper.findAll('button').find(b => b.text() === 'Send Test Event')
    await testButton().trigger('click')
    await flushPromises()
    expect(wrapper.get('[role="status"]').text()).toBe('Test event sent to siem.corp.example:6514 (Tls, OctetCounting).')

    globalThis.fetch.mockImplementation(() => Promise.resolve(jsonResponse({ success: false, target: 'x:1 (Tcp, OctetCounting)', error: 'Connection refused' })))
    await testButton().trigger('click')
    await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toBe('Test event to x:1 (Tcp, OctetCounting) failed: Connection refused')
  })
})
