<script setup>
// D-216: the break-glass accounts section of Identity Providers. A sign-in by
// one of these accounts writes a BreakGlassLogon audit event and emails the
// BreakGlassLogon notification's recipients (set on the Notifications page).
// There's no separate sign-in path: the account signs in like any other.
import { ref, onMounted } from 'vue'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { formatDate } from '../../utils/formatDate'

const accounts = ref([])
const error = ref(null)
const saveError = ref(null)
const saving = ref(false)
const adding = ref({ account: '', displayName: '', notes: '' })

async function load() {
  error.value = null
  try {
    const response = await fetch('/api/admin/break-glass-accounts')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
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
      saveError.value = problem?.detail ?? `Add failed: ${response.status}`
      return
    }
    adding.value = { account: '', displayName: '', notes: '' }
    await load()
  } finally {
    saving.value = false
  }
}

async function remove(account) {
  if (!(await confirmDelete(`Remove "${account.displayName}" from the break-glass accounts? Its sign-ins will no longer raise an alert.`, 'Remove'))) return
  const response = await fetch(`/api/admin/break-glass-accounts/${account.breakGlassAccountKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Remove failed: ${response.status}`
    return
  }
  await load()
}
</script>

<template>
  <section class="break-glass">
    <h3>Break-glass accounts</h3>
    <p>
      Emergency accounts whose sign-in raises an alert: a <code>BreakGlassLogon</code> event in the Audit Log, and an email to the
      <strong>BreakGlassLogon</strong> notification's recipients (Admin &gt; Notifications). They sign in like any other account.
    </p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="accounts.length === 0 && !error">No break-glass accounts.</p>
    <table v-else-if="accounts.length">
      <thead>
        <tr><th>Account</th><th>Identifier</th><th>Notes</th><th>Added</th><th></th></tr>
      </thead>
      <tbody>
        <tr v-for="a in accounts" :key="a.breakGlassAccountKey">
          <td>{{ a.displayName }}</td>
          <td><code>{{ a.externalIdentifier }}</code></td>
          <td>{{ a.notes }}</td>
          <td>{{ formatDate(a.createdDate) }}<template v-if="a.createdByName"> by {{ a.createdByName }}</template></td>
          <td><button type="button" @click="remove(a)">Remove</button></td>
        </tr>
      </tbody>
    </table>

    <form @submit.prevent="add">
      <p>
        <label class="field-label"><span class="field-label-text">Account:</span> <input v-model="adding.account" maxlength="300" placeholder=".\Administrator" required /></label>
        <small>DOMAIN\name, or .\name for a local account on this server (stored as its SID); or a SID, or an OIDC/SAML user identifier.</small>
      </p>
      <p><label class="field-label"><span class="field-label-text">Display name:</span> <input v-model="adding.displayName" maxlength="300" placeholder="(the account, if blank)" /></label></p>
      <p><label class="field-label"><span class="field-label-text">Notes:</span> <input v-model="adding.notes" maxlength="1000" placeholder="Where the credential is kept in CyberArk" /></label></p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" :disabled="saving">Add break-glass account</button>
    </form>
  </section>
</template>
