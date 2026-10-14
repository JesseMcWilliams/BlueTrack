<script setup>
// D-216: the break-glass accounts section of Identity Providers. A sign-in by
// one of these accounts writes a BreakGlassLogon audit event and emails the
// BreakGlassLogon notification's recipients (set on the Notifications page).
// There's no separate sign-in path: the account signs in like any other.
import { ref, onMounted } from 'vue'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { formatDate } from '../../utils/formatDate'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const accounts = ref([])
const error = ref(null)
const saveError = ref(null)
const saving = ref(false)
// The audit event and notification name -- an identifier, not translated.
const EVENT_NAME = 'BreakGlassLogon'
const adding = ref({ account: '', displayName: '', notes: '' })

async function load() {
  error.value = null
  try {
    const response = await fetch('/api/admin/break-glass-accounts')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    accounts.value = await response.json()
  } catch (err) {
    error.value = err.message
  }
}

onMounted(load)

async function add() {
  saveError.value = null
  saving.value = true
  try {
    const response = await fetch('/api/admin/break-glass-accounts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(adding.value)
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      saveError.value = problem?.detail ?? t('admin.breakGlassAccounts.addFailed', { status: response.status })
      return
    }
    adding.value = { account: '', displayName: '', notes: '' }
    await load()
  } finally {
    saving.value = false
  }
}

async function remove(account) {
  if (!(await confirmDelete(t('admin.breakGlassAccounts.confirmRemove', { name: account.displayName }), t('common.remove')))) return
  const response = await fetch(`/api/admin/break-glass-accounts/${account.breakGlassAccountKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('admin.breakGlassAccounts.removeFailed', { status: response.status })
    return
  }
  await load()
}
</script>

<template>
  <section class="break-glass">
    <h3>{{ t('admin.breakGlassAccounts.title') }}</h3>
    <i18n-t keypath="admin.breakGlassAccounts.intro" tag="p" scope="global">
      <template #event><code>{{ EVENT_NAME }}</code></template>
      <template #notification><strong>{{ EVENT_NAME }}</strong></template>
    </i18n-t>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="accounts.length === 0 && !error">{{ t('admin.breakGlassAccounts.noAccounts') }}</p>
    <table v-else-if="accounts.length">
      <thead>
        <tr><th>{{ t('admin.breakGlassAccounts.account') }}</th><th>{{ t('admin.breakGlassAccounts.identifier') }}</th><th>{{ t('admin.breakGlassAccounts.notes') }}</th><th>{{ t('admin.breakGlassAccounts.added') }}</th><th></th></tr>
      </thead>
      <tbody>
        <tr v-for="a in accounts" :key="a.breakGlassAccountKey">
          <td>{{ a.displayName }}</td>
          <td><code>{{ a.externalIdentifier }}</code></td>
          <td>{{ a.notes }}</td>
          <td>{{ formatDate(a.createdDate) }}<template v-if="a.createdByName">{{ ' ' + t('admin.breakGlassAccounts.addedBy', { name: a.createdByName }) }}</template></td>
          <td><button type="button" @click="remove(a)">{{ t('common.remove') }}</button></td>
        </tr>
      </tbody>
    </table>

    <form @submit.prevent="add">
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.breakGlassAccounts.accountLabel') }}</span> <input v-model="adding.account" maxlength="300" :placeholder="t('admin.breakGlassAccounts.accountPlaceholder')" required /></label>
        <small>{{ t('admin.breakGlassAccounts.accountHint') }}</small>
      </p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.breakGlassAccounts.displayNameLabel') }}</span> <input v-model="adding.displayName" maxlength="300" :placeholder="t('admin.breakGlassAccounts.displayNamePlaceholder')" /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.breakGlassAccounts.notesLabel') }}</span> <input v-model="adding.notes" maxlength="1000" :placeholder="t('admin.breakGlassAccounts.notesPlaceholder')" /></label></p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" :disabled="saving">{{ t('admin.breakGlassAccounts.addButton') }}</button>
    </form>
  </section>
</template>
