import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { flashDismiss, useFlashState } from '../../composables/useFlash'

// UI review theme 1 and 3: the edit pages that used to answer a refused save with only "save failed: 400" give the
// API's reason, and a save that goes back to the list leaves a "saved" line (FlashMessage, App.vue).
const push = vi.hoisted(() => vi.fn())
vi.mock('vue-router', () => ({ useRouter: () => ({ push }) }))

import ApplicationEdit from './ApplicationEdit.vue'
import RoleEdit from './RoleEdit.vue'
import IdentityProviderEdit from './IdentityProviderEdit.vue'
import ImportMappingProfileEdit from './ImportMappingProfileEdit.vue'
import NotificationRecipientCreate from './NotificationRecipientCreate.vue'
import FieldMetadataEdit from './FieldMetadataEdit.vue'

function response(body, { ok = true, status = 200 } = {}) {
  return { ok, status, json: () => (body === undefined ? Promise.reject(new SyntaxError('no body')) : Promise.resolve(body)) }
}

// Every GET answers with an empty list; the save (POST) answers as the test says.
function mockApi(postResponse) {
  globalThis.fetch = vi.fn((url, init) => Promise.resolve(init?.method === 'POST' ? postResponse : response([])))
}

const pages = [
  { name: 'Application', component: ApplicationEdit, label: 'Name:', typed: 'Payroll', route: 'admin-application-mapping', fallback: 'Save failed: 500' },
  { name: 'Role', component: RoleEdit, label: 'Role Name:', typed: 'Auditors', route: 'admin-roles-permissions', fallback: 'Save failed: 500' },
  { name: 'Identity provider', component: IdentityProviderEdit, label: 'Display Name:', typed: 'Corporate SSO', route: 'admin-identity-providers', fallback: 'Save failed: 500' },
  { name: 'Import mapping profile', component: ImportMappingProfileEdit, label: 'Profile Name:', typed: 'CMDB layout', route: 'admin-import-mapping-profiles', fallback: 'Save failed: 500' },
  { name: 'Notification recipient', component: NotificationRecipientCreate, label: 'Email:', typed: 'ops@example.com', route: 'admin-notifications', fallback: 'Add failed: 500' },
  { name: 'Field definition', component: FieldMetadataEdit, label: 'Display Label:', typed: 'Owner', route: 'admin-field-metadata', fallback: 'Save failed: 500' }
]

async function mountPage(component, label, typed) {
  const wrapper = mount(component)
  await flushPromises()
  const field = wrapper.findAll('label').find(l => l.text().startsWith(label)).find('input')
  await field.setValue(typed)
  return wrapper
}

describe.each(pages)('$name edit page', ({ component, label, typed, route, fallback }) => {
  beforeEach(() => {
    push.mockClear()
    flashDismiss()
  })
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it("shows the API's reason when a save is refused, and stays on the page", async () => {
    mockApi(response({ detail: 'That name is already taken.' }, { ok: false, status: 409 }))
    const wrapper = await mountPage(component, label, typed)

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toBe('That name is already taken.')
    expect(push).not.toHaveBeenCalled()
    expect(useFlashState().message).toBeNull()
  })

  it('falls back to the status when the refusal has no reason', async () => {
    mockApi(response(undefined, { ok: false, status: 500 }))
    const wrapper = await mountPage(component, label, typed)

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toBe(fallback)
  })

  it('reads a model-validation refusal too', async () => {
    mockApi(response({ title: 'One or more validation errors occurred.', errors: { Name: ['The Name field is required.'] } }, { ok: false, status: 400 }))
    const wrapper = await mountPage(component, label, typed)

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toBe('The Name field is required.')
  })

  it('a save goes back to the list and leaves a saved message naming it', async () => {
    mockApi(response({}, { status: 201 }))
    const wrapper = await mountPage(component, label, typed)

    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(push).toHaveBeenCalledWith({ name: route })
    expect(useFlashState().message).toBe(`${typed} saved.`)
  })
})
