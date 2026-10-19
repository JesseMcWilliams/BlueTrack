import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { createRouter, createMemoryHistory } from 'vue-router'
import RiskExceptionEdit from './RiskExceptionEdit.vue'
import Combobox from '../components/Combobox.vue'

// UI review W3-1 and W3-3: a failed save, extend or revoke keeps the form or detail on screen with its error beside
// it, and what worked is confirmed. Revoke asks first.
vi.mock('../composables/useConfirmDialog', () => ({
  confirmDelete: vi.fn().mockResolvedValue(true)
}))
import { confirmDelete } from '../composables/useConfirmDialog'

function jsonResponse(body, { ok = true, status = 200 } = {}) {
  return { ok, status, json: () => (body === undefined ? Promise.reject(new Error('no body')) : Promise.resolve(body)) }
}

const active = {
  exceptionKey: 5, exceptionID: 'EX-0005', accountKey: 1234, applicationKey: null, justification: 'Legacy system',
  approvalDate: '2026-01-05T00:00:00Z', approvedByName: 'Pat Approver', reviewDate: '2026-12-31T00:00:00Z',
  statusName: 'Active', externalTicketReference: 'CHG-1'
}

function makeRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/accounts/:accountKey', name: 'account-progress-detail', component: { template: '<div />' } },
      { path: '/exceptions/:exceptionKey', name: 'risk-exception-edit', component: { template: '<div />' } }
    ]
  })
}

function routeFetch(handlers) {
  globalThis.fetch = vi.fn((url, options) => {
    const key = `${options?.method ?? 'GET'} ${url}`
    const handler = handlers[key]
    if (!handler) throw new Error(`Unexpected fetch: ${key}`)
    return Promise.resolve(typeof handler === 'function' ? handler() : handler)
  })
}

async function mountEdit(props = {}) {
  const wrapper = mount(RiskExceptionEdit, { props, global: { plugins: [makeRouter()] } })
  await flushPromises()
  return wrapper
}

describe('RiskExceptionEdit.vue', () => {
  beforeEach(() => {
    confirmDelete.mockResolvedValue(true)
  })
  afterEach(() => {
    vi.restoreAllMocks()
  })

  const picked = { accountKey: 1234, userName: 'svc_payroll', accountName: 'svc_payroll', address: 'pay01.example.com', stageName: 'Discovery' }
  const ticketInput = (wrapper) => wrapper.findAll('label').find(l => l.text().startsWith('External Ticket')).find('input')

  async function fillCreateForm(wrapper) {
    wrapper.findComponent(Combobox).vm.$emit('update:modelValue', picked) // what choosing a result does
    await wrapper.find('textarea').setValue('Legacy system, retiring Q3')
    await wrapper.find('input[type="date"]').setValue('2026-12-31')
    await ticketInput(wrapper).setValue('CHG-77')
  }

  describe('create', () => {
    it.each([
      [403, null, 'You do not have the ApproveExceptions permission.'],
      [400, { detail: 'Account 1234 does not exist.' }, 'Account 1234 does not exist.'],
      [500, undefined, 'Request failed: 500']
    ])('a %i keeps the form and what was typed, with the reason beside it', async (status, body, message) => {
      routeFetch({ 'GET /api/applications': jsonResponse([]), 'POST /api/risk-exceptions': jsonResponse(body, { ok: false, status }) })
      const wrapper = await mountEdit()
      await fillCreateForm(wrapper)

      await wrapper.find('form').trigger('submit')
      await flushPromises()

      expect(wrapper.find('[role="alert"]').text()).toBe(message)
      expect(wrapper.find('form').exists()).toBe(true)
      expect(wrapper.findComponent(Combobox).props('modelValue')).toEqual(picked)
      expect(wrapper.find('textarea').element.value).toBe('Legacy system, retiring Q3')
      expect(ticketInput(wrapper).element.value).toBe('CHG-77')
      expect(wrapper.find('button[type="submit"]').attributes('disabled')).toBeUndefined() // can try again
    })

    it('asks for an account to be chosen instead of sending a blank one', async () => {
      routeFetch({ 'GET /api/applications': jsonResponse([]) })
      const wrapper = await mountEdit()
      await wrapper.find('textarea').setValue('Legacy system')
      await wrapper.find('input[type="date"]').setValue('2026-12-31')

      await wrapper.find('form').trigger('submit')
      await flushPromises()

      expect(wrapper.find('[role="alert"]').text()).toBe('Choose an account from the list.')
      expect(globalThis.fetch.mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false)
    })

    it('sends the key of the chosen account', async () => {
      routeFetch({ 'GET /api/applications': jsonResponse([]), 'POST /api/risk-exceptions': jsonResponse({ exceptionKey: 9 }, { status: 201 }) })
      const wrapper = await mountEdit()
      await fillCreateForm(wrapper)

      await wrapper.find('form').trigger('submit')
      await flushPromises()

      const post = globalThis.fetch.mock.calls.find(([, init]) => init?.method === 'POST')
      expect(JSON.parse(post[1].body).accountKey).toBe(1234)
    })

    it('finds accounts by what is typed, and shows who and where each one is', async () => {
      vi.useFakeTimers()
      globalThis.fetch = vi.fn((url) => Promise.resolve(url === '/api/applications' ? jsonResponse([]) : jsonResponse([picked])))
      const wrapper = mount(RiskExceptionEdit, { global: { plugins: [makeRouter()] } })
      await vi.advanceTimersByTimeAsync(0)
      await wrapper.find('input[role="combobox"]').setValue('payroll')
      await vi.advanceTimersByTimeAsync(300)
      await flushPromises()

      expect(globalThis.fetch).toHaveBeenCalledWith('/api/account-progress?search=payroll&pageSize=10')
      expect(wrapper.find('[role="option"]').text()).toContain('svc_payroll (pay01.example.com)')
      vi.useRealTimers()
    })

    it('goes to the new exception when it is created', async () => {
      routeFetch({ 'GET /api/applications': jsonResponse([]), 'POST /api/risk-exceptions': jsonResponse({ exceptionKey: 9 }, { status: 201 }) })
      const router = makeRouter()
      const wrapper = mount(RiskExceptionEdit, { global: { plugins: [router] } })
      await flushPromises()
      await fillCreateForm(wrapper)

      await wrapper.find('form').trigger('submit')
      await flushPromises()

      expect(router.currentRoute.value.params.exceptionKey).toBe('9')
    })

    it('clears the earlier error when the next try is made', async () => {
      let first = true
      routeFetch({
        'GET /api/applications': jsonResponse([]),
        'POST /api/risk-exceptions': () => {
          if (first) {
            first = false
            return jsonResponse(undefined, { ok: false, status: 500 })
          }
          return jsonResponse({ exceptionKey: 9 }, { status: 201 })
        }
      })
      const wrapper = await mountEdit()
      await fillCreateForm(wrapper)
      await wrapper.find('form').trigger('submit')
      await flushPromises()
      expect(wrapper.find('[role="alert"]').exists()).toBe(true)

      await wrapper.find('form').trigger('submit')
      await flushPromises()

      expect(wrapper.find('[role="alert"]').exists()).toBe(false)
    })
  })

  describe('edit', () => {
    // UI review W3-4
    it('names the account and links to it, or the application by name', async () => {
      routeFetch({ 'GET /api/applications': jsonResponse([]), 'GET /api/risk-exceptions/5': jsonResponse({ ...active, accountName: 'svc_payroll', accountAddress: 'pay01.example.com' }) })
      const account = await mountEdit({ exceptionKey: 5 })
      const link = account.find('dd a')
      expect(link.text()).toBe('svc_payroll (pay01.example.com)')
      expect(link.attributes('href')).toBe('/accounts/1234')

      routeFetch({ 'GET /api/applications': jsonResponse([]), 'GET /api/risk-exceptions/5': jsonResponse({ ...active, accountKey: null, applicationKey: 3, applicationName: 'Payroll' }) })
      const application = await mountEdit({ exceptionKey: 5 })
      expect(application.text()).toContain('Application: Payroll')
    })

    it('falls back to the key when the name is missing', async () => {
      routeFetch({ 'GET /api/applications': jsonResponse([]), 'GET /api/risk-exceptions/5': jsonResponse({ ...active }) })
      const wrapper = await mountEdit({ exceptionKey: 5 })
      expect(wrapper.text()).toContain('Account #1234')
    })

    function base(extra = {}) {
      return { 'GET /api/applications': jsonResponse([]), 'GET /api/risk-exceptions/5': jsonResponse({ ...active }), ...extra }
    }

    it('a failed load shows only the error', async () => {
      routeFetch({ 'GET /api/applications': jsonResponse([]), 'GET /api/risk-exceptions/5': jsonResponse(null, { ok: false, status: 404 }) })
      const wrapper = await mountEdit({ exceptionKey: 5 })

      expect(wrapper.find('[role="alert"]').text()).toBe('Request failed: 404')
      expect(wrapper.find('dl').exists()).toBe(false)
      expect(wrapper.find('form').exists()).toBe(false)
    })

    it('extending the review date says so, and shows the new date', async () => {
      routeFetch(base({ 'PUT /api/risk-exceptions/5/extend-review': jsonResponse(null, { status: 204 }) }))
      const wrapper = await mountEdit({ exceptionKey: 5 })

      await wrapper.find('input[type="date"]').setValue('2027-06-30')
      await wrapper.findAll('button').find(b => b.text() === 'Extend Review Date').trigger('click')
      await flushPromises()

      expect(wrapper.find('[role="status"]').text()).toContain('The review date is now')
      expect(wrapper.find('dl').text()).toContain('2027')
    })

    it("a failed extend keeps the detail and the forms, with the API's reason", async () => {
      routeFetch(base({ 'PUT /api/risk-exceptions/5/extend-review': jsonResponse({ detail: 'The new review date must be in the future.' }, { ok: false, status: 400 }) }))
      const wrapper = await mountEdit({ exceptionKey: 5 })

      await wrapper.findAll('button').find(b => b.text() === 'Extend Review Date').trigger('click')
      await flushPromises()

      expect(wrapper.find('[role="alert"]').text()).toBe('The new review date must be in the future.')
      expect(wrapper.find('dl').text()).toContain('Legacy system')
      expect(wrapper.findAll('button').some(b => b.text() === 'Extend Review Date')).toBe(true)
      expect(wrapper.findAll('button').some(b => b.text() === 'Revoke Exception')).toBe(true)
      expect(wrapper.find('[role="status"]').exists()).toBe(false)
    })

    it('revoking asks first, naming the exception and the reason, and does nothing when declined', async () => {
      confirmDelete.mockResolvedValue(false)
      routeFetch(base())
      const wrapper = await mountEdit({ exceptionKey: 5 })
      await wrapper.find('input[maxlength="1000"]').setValue('System retired')

      await wrapper.findAll('button').find(b => b.text() === 'Revoke Exception').trigger('click')
      await flushPromises()

      expect(confirmDelete).toHaveBeenCalledTimes(1)
      const [message, label] = confirmDelete.mock.calls[0]
      expect(message.title).toBe('Revoke risk exception EX-0005?')
      expect(message.details).toEqual(['Reason: System retired'])
      expect(label).toBe('Revoke Exception')
      expect(globalThis.fetch.mock.calls.some(([, o]) => o?.method === 'PUT')).toBe(false)
      expect(wrapper.find('dl').text()).toContain('Active')
    })

    it('a confirmed revoke sends the reason, shows Revoked and says so', async () => {
      routeFetch(base({ 'PUT /api/risk-exceptions/5/revoke': jsonResponse(null, { status: 204 }) }))
      const wrapper = await mountEdit({ exceptionKey: 5 })
      await wrapper.find('input[maxlength="1000"]').setValue('System retired')

      await wrapper.findAll('button').find(b => b.text() === 'Revoke Exception').trigger('click')
      await flushPromises()

      const put = globalThis.fetch.mock.calls.find(([, o]) => o?.method === 'PUT')
      expect(JSON.parse(put[1].body)).toEqual({ reason: 'System retired' })
      expect(wrapper.find('dl').text()).toContain('Revoked')
      expect(wrapper.find('[role="status"]').text()).toBe('The exception was revoked.')
      expect(wrapper.findAll('button').some(b => b.text() === 'Revoke Exception')).toBe(false) // no actions on a revoked exception
    })

    it('a failed revoke keeps the detail, with the reason, and the revoke form still open', async () => {
      routeFetch(base({ 'PUT /api/risk-exceptions/5/revoke': jsonResponse({ detail: 'Not allowed.' }, { ok: false, status: 403 }) }))
      const wrapper = await mountEdit({ exceptionKey: 5 })
      await wrapper.find('input[maxlength="1000"]').setValue('System retired')

      await wrapper.findAll('button').find(b => b.text() === 'Revoke Exception').trigger('click')
      await flushPromises()

      expect(wrapper.find('[role="alert"]').text()).toBe('Not allowed.')
      expect(wrapper.find('dl').text()).toContain('Active')
      expect(wrapper.find('input[maxlength="1000"]').element.value).toBe('System retired')
    })

    it('Revoke stays disabled until a reason is typed', async () => {
      routeFetch(base())
      const wrapper = await mountEdit({ exceptionKey: 5 })

      const revoke = wrapper.findAll('button').find(b => b.text() === 'Revoke Exception')
      expect(revoke.attributes('disabled')).toBeDefined()
      await wrapper.find('input[maxlength="1000"]').setValue('   ')
      expect(revoke.attributes('disabled')).toBeDefined()
    })
  })
})
