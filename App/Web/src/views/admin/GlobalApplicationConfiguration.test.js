import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import GlobalApplicationConfiguration from './GlobalApplicationConfiguration.vue'

// D-224: the Site URL field on Global Application Configuration.

function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

const baseConfig = {
  idleTimeoutMinutes: 30,
  breadcrumbPosition: 'TopLeft',
  exceptionIdPattern: 'RE-{yyyy}-{seq:0000}',
  lockTimeoutMinutes: 30,
  retentionDays: null,
  logReadEvents: false,
  backupFolder: null,
  activeRiskAlgorithm: 'DominantPlusTail',
  enforceRiskExceptionSegregationOfDuties: false,
  dataFeedRunTime: '04:00',
  dataFeedRunRetentionDays: 30,
  scheduledBackupTime: null,
  scheduledBackupRetentionDays: 14,
  businessHoursStart: '08:00',
  businessHoursEnd: '17:00',
  businessDays: 'Mon,Tue,Wed,Thu,Fri',
  bulkEditMaxAccounts: 500,
  exportMaxRows: 100000,
  apiKeyDefaultLifetimeDays: 90,
  apiRateLimitPerMinute: 600,
  apiKeyReminderLeadDays: 7,
  apiKeyExpiredRetentionDays: 365,
  safeDecomMode: 'Off',
  safeDecomValue: null,
  accountDecomMode: 'Off',
  accountDecomValue: null,
  safeIgnoreMode: 'Off',
  safeIgnoreValue: null,
  regexSupported: false,
  siteUrl: null,
  formatCulture: 'en-US'
}

// GETs return the queue in order (the last one repeats); PUTs go to putHandler.
function mockFetch(gets, putHandler = () => ({ ok: true, status: 204 })) {
  const queue = [...gets]
  globalThis.fetch = vi.fn((url, options) => {
    if ((options?.method ?? 'GET') === 'PUT') return Promise.resolve(putHandler(JSON.parse(options.body)))
    const next = queue.length > 1 ? queue.shift() : queue[0]
    return Promise.resolve(jsonResponse(next))
  })
}

function siteUrlInput(wrapper) {
  return wrapper.find('input[type="url"]')
}

describe('GlobalApplicationConfiguration.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('shows the saved Site URL', async () => {
    mockFetch([{ ...baseConfig, siteUrl: 'https://bluetrack.company.com' }])
    const wrapper = mount(GlobalApplicationConfiguration)
    await flushPromises()

    expect(wrapper.text()).toContain('Site URL')
    expect(siteUrlInput(wrapper).element.value).toBe('https://bluetrack.company.com')
  })

  it('saves the Site URL trimmed, then shows the form the API stored it in', async () => {
    let sent = null
    mockFetch([baseConfig, { ...baseConfig, siteUrl: 'https://bluetrack.company.com' }], body => {
      sent = body
      return { ok: true, status: 204 }
    })
    const wrapper = mount(GlobalApplicationConfiguration)
    await flushPromises()

    await siteUrlInput(wrapper).setValue('  https://BlueTrack.company.com/  ')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(sent.siteUrl).toBe('https://BlueTrack.company.com/')
    expect(wrapper.text()).toContain('Saved.')
    expect(siteUrlInput(wrapper).element.value).toBe('https://bluetrack.company.com')
  })

  it('sends a blank Site URL as "" (not set), never null (unchanged)', async () => {
    let sent = null
    mockFetch([{ ...baseConfig, siteUrl: 'https://bluetrack.company.com' }, baseConfig], body => {
      sent = body
      return { ok: true, status: 204 }
    })
    const wrapper = mount(GlobalApplicationConfiguration)
    await flushPromises()

    await siteUrlInput(wrapper).setValue('')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(sent.siteUrl).toBe('')
  })

  it('shows the API\'s reason when the Site URL is refused', async () => {
    mockFetch([baseConfig], () => jsonResponse({ title: 'Invalid site URL', detail: 'The Site URL must use https://. http:// is allowed only for localhost.' }, false, 400))
    const wrapper = mount(GlobalApplicationConfiguration)
    await flushPromises()

    await siteUrlInput(wrapper).setValue('http://bluetrack.company.com')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('Save failed: The Site URL must use https://')
    expect(wrapper.text()).not.toContain('Saved.')
  })
})

// D-294: the number and date format of emails and run summaries.
describe('GlobalApplicationConfiguration.vue number and date format', () => {
  afterEach(() => vi.restoreAllMocks())

  it('shows an example in the chosen format and saves the culture name', async () => {
    let sent = null
    mockFetch([{ ...baseConfig, formatCulture: 'de-DE' }], body => { sent = body; return { ok: true, status: 204 } })
    const wrapper = mount(GlobalApplicationConfiguration)
    await flushPromises()

    const input = wrapper.find('input[list="format-cultures"]')
    expect(input.element.value).toBe('de-DE')
    expect(wrapper.text()).toContain('Example: 1.234.567,5, 21.10.2026.')

    await input.setValue('en-GB')
    expect(wrapper.text()).toContain('Example: 1,234,567.5, 21/10/2026.')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(sent.formatCulture).toBe('en-GB')
  })

  it('says so when the browser does not know the name', async () => {
    mockFetch([{ ...baseConfig, formatCulture: 'not a culture!' }])
    const wrapper = mount(GlobalApplicationConfiguration)
    await flushPromises()
    expect(wrapper.text()).toContain('Not a culture name this browser knows.')
  })
})
