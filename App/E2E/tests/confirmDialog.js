/**
 * Clicks the shared ConfirmDialog's own confirm button (D-128,
 * App/Web/src/components/ConfirmDialog.vue) -- every Delete button in the
 * app now shows this dialog and waits for it before actually deleting.
 * @param {import('@playwright/test').Page} page
 * @param {string} [label] Defaults to 'Delete'; pass the dialog's actual
 *   confirmLabel for a non-delete confirmation (e.g. 'Deactivate').
 */
export async function confirmDelete(page, label = 'Delete') {
  await page.getByRole('dialog').getByRole('button', { name: label }).click()
}
