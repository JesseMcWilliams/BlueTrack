import { describe, it, expect, vi, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import AccountProgressImport from './AccountProgressImport.vue'

// D-318: reading an Accounts export back with changed values.
function jsonResponse(body, ok = true, status = 200) {
  return { ok, status, json: () => Promise.resolve(body) }
}

async function chooseFileAndImport(wrapper, reason = '') {
  await wrapper.find('button').trigger('click')   // Import changes...
  const input = wrapper.find('input[type="file"]')
  Object.defineProperty(input.element, 'files', { value: [new File(['Account Key,Owner\n1,Jane\n'], 'accounts.csv', { type: 'text/csv' })] })
  await input.trigger('change')
  if (reason) await wrapper.findAll('input')[1].setValue(reason)
  await wrapper.find('form').trigger('submit')
  await flushPromises()
}

describe('AccountProgressImport.vue', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('sends the file and the reason, says what changed and lists the rows it could not apply', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({ rows: 3, updated: 1, unchanged: 1, problems: [{ rowNumber: 4, accountKey: 9, reason: 'No account has this key.' }] })))
    const wrapper = mount(AccountProgressImport)

    await chooseFileAndImport(wrapper, 'Quarterly review')

    const [url, init] = globalThis.fetch.mock.calls[0]
    expect(url).toBe('/api/account-progress/import')
    expect(init.body.get('file').name).toBe('accounts.csv')
    expect(init.body.get('reason')).toBe('Quarterly review')
    expect(wrapper.text()).toContain('Read 3 rows: 1 accounts changed, 1 already as the file says, 1 not applied.')
    expect(wrapper.text()).toContain('Row 4: No account has this key.')
    expect(wrapper.emitted('imported')).toHaveLength(1)
  })

  it('shows why a file was refused', async () => {
    globalThis.fetch = vi.fn(() => Promise.resolve(jsonResponse({ detail: 'The file needs an "Account Key" column.' }, false, 400)))
    const wrapper = mount(AccountProgressImport)

    await chooseFileAndImport(wrapper)

    expect(wrapper.find('[role="alert"]').text()).toBe('The file needs an "Account Key" column.')
    expect(wrapper.emitted('imported')).toBeUndefined()
  })
})
