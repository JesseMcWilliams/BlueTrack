<script setup>
// Design_Notifications.md, D-115: SMTP settings + a dedicated recipient
// list (web.app_user.Email is unusable today -- see that doc) + a test
// email, against /api/admin/notifications (NotificationsController).
import { ref, onMounted } from 'vue'

const config = ref(null)
const plaintextPassword = ref('')
const configError = ref(null)
const configSaved = ref(false)

const recipients = ref([])
const newRecipient = ref({ email: '', displayName: '', isActive: true })
const recipientsError = ref(null)

const testResult = ref(null)
const testing = ref(false)

const loading = ref(true)
const loadError = ref(null)

async function load() {
  loading.value = true
  loadError.value = null
  try {
    const [configResponse, recipientsResponse] = await Promise.all([
      fetch('/api/admin/notifications/config'),
      fetch('/api/admin/notifications/recipients')
    ])
    if (!configResponse.ok) throw new Error(`Config request failed: ${configResponse.status}`)
    if (!recipientsResponse.ok) throw new Error(`Recipients request failed: ${recipientsResponse.status}`)
    config.value = await configResponse.json()
    recipients.value = await recipientsResponse.json()
  } catch (err) {
    loadError.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function saveConfig() {
  configError.value = null
  configSaved.value = false
  const response = await fetch('/api/admin/notifications/config', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      smtpHost: config.value.smtpHost,
      smtpPort: Number(config.value.smtpPort),
      enableStartTls: config.value.enableStartTls,
      authMethod: config.value.authMethod,
      username: config.value.username,
      fromAddress: config.value.fromAddress,
      fromDisplayName: config.value.fromDisplayName,
      plaintextPassword: plaintextPassword.value || null
    })
  })
  if (!response.ok) {
    configError.value = `Save failed: ${response.status}`
    return
  }
  plaintextPassword.value = ''
  configSaved.value = true
  await load()
}

async function addRecipient() {
  recipientsError.value = null
  const response = await fetch('/api/admin/notifications/recipients', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(newRecipient.value)
  })
  if (!response.ok) {
    recipientsError.value = `Add failed: ${response.status}`
    return
  }
  newRecipient.value = { email: '', displayName: '', isActive: true }
  await load()
}

async function toggleRecipientActive(recipient) {
  recipientsError.value = null
  const response = await fetch(`/api/admin/notifications/recipients/${recipient.recipientKey}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email: recipient.email, displayName: recipient.displayName, isActive: !recipient.isActive })
  })
  if (!response.ok) {
    recipientsError.value = `Update failed: ${response.status}`
    return
  }
  await load()
}

async function removeRecipient(recipient) {
  recipientsError.value = null
  const response = await fetch(`/api/admin/notifications/recipients/${recipient.recipientKey}`, { method: 'DELETE' })
  if (!response.ok) {
    recipientsError.value = `Delete failed: ${response.status}`
    return
  }
  await load()
}

async function sendTestEmail() {
  testing.value = true
  testResult.value = null
  try {
    const response = await fetch('/api/admin/notifications/test', { method: 'POST' })
    testResult.value = response.ok ? await response.json() : { success: false, error: `Request failed: ${response.status}` }
  } finally {
    testing.value = false
  }
}
</script>

<template>
  <div>
    <h2>Notifications</h2>
    <p>SMTP settings and the recipient list for BlueTrack's automated alerts (e.g. DevFakeAuth left enabled too long, D-114).</p>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <template v-else>
      <h3>SMTP Configuration</h3>
      <p v-if="configError" role="alert">{{ configError }}</p>
      <p v-if="configSaved" role="status">Saved.</p>
      <form @submit.prevent="saveConfig">
        <p><label class="field-label"><span class="field-label-text">SMTP Host:</span> <input v-model="config.smtpHost" placeholder="smtp.company.com" /></label></p>
        <p><label class="field-label"><span class="field-label-text">SMTP Port:</span> <input v-model.number="config.smtpPort" type="number" /></label></p>
        <p><label><input v-model="config.enableStartTls" type="checkbox" /> Enable STARTTLS</label></p>
        <p>
          <label class="field-label">
            <span class="field-label-text">Auth Method:</span>
            <select v-model="config.authMethod">
              <option value="None">None (open relay)</option>
              <option value="Basic">Basic (username/password)</option>
            </select>
          </label>
        </p>
        <template v-if="config.authMethod === 'Basic'">
          <p><label class="field-label"><span class="field-label-text">Username:</span> <input v-model="config.username" /></label></p>
          <p>
            <label class="field-label">
              <span class="field-label-text">Password:</span>
              <input v-model="plaintextPassword" type="password"
                :placeholder="config.passwordSecretReference ? '(already set -- leave blank to keep)' : '(none set)'" />
            </label>
          </p>
        </template>
        <p><label class="field-label"><span class="field-label-text">From Address:</span> <input v-model="config.fromAddress" placeholder="bluetrack@company.com" /></label></p>
        <p><label class="field-label"><span class="field-label-text">From Display Name:</span> <input v-model="config.fromDisplayName" placeholder="BlueTrack" /></label></p>
        <button type="submit" class="btn-primary">Save</button>
      </form>

      <h3>Test Connection</h3>
      <p>Sends a real test email to every active recipient below, using the saved SMTP configuration.</p>
      <button :disabled="testing" @click="sendTestEmail">Send Test Email</button>
      <p v-if="testResult?.success" role="status">Sent successfully to {{ testResult.recipientCount }} recipient(s).</p>
      <p v-else-if="testResult" role="alert">Failed: {{ testResult.error }}</p>

      <h3>Recipients</h3>
      <p v-if="recipientsError" role="alert">{{ recipientsError }}</p>
      <table v-if="recipients.length > 0">
        <thead>
          <tr><th>Email</th><th>Display Name</th><th>Active</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="recipient in recipients" :key="recipient.recipientKey">
            <td>{{ recipient.email }}</td>
            <td>{{ recipient.displayName }}</td>
            <td>{{ recipient.isActive ? 'Yes' : 'No' }}</td>
            <td>
              <button type="button" @click="toggleRecipientActive(recipient)">{{ recipient.isActive ? 'Deactivate' : 'Activate' }}</button>
              <button type="button" @click="removeRecipient(recipient)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>
      <p v-else>No recipients configured yet -- alerts have nowhere to go until at least one is added below.</p>

      <h4>Add Recipient</h4>
      <form @submit.prevent="addRecipient">
        <p><label class="field-label"><span class="field-label-text">Email:</span> <input v-model="newRecipient.email" type="email" required /></label></p>
        <p><label class="field-label"><span class="field-label-text">Display Name:</span> <input v-model="newRecipient.displayName" /></label></p>
        <button type="submit" class="btn-primary">Add Recipient</button>
      </form>
    </template>
  </div>
</template>
