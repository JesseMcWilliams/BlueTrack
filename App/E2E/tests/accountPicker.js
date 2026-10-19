/**
 * Picks an account in the Risk Exception create form's typeahead (UI review W3-2): types part of its name, waits for
 * the result, and chooses it with the keyboard.
 * @param {import('@playwright/test').Page} page
 * @param {string} name What to type, and what the chosen option must contain (e.g. 'TestAccount03').
 */
export async function pickAccount(page, name) {
  const combobox = page.getByRole('combobox', { name: 'Account:' })
  await combobox.pressSequentially(name)
  const option = page.getByRole('option', { name: new RegExp(name) }).first()
  await option.waitFor()
  await combobox.press('ArrowDown')
  await combobox.press('Enter')
}
