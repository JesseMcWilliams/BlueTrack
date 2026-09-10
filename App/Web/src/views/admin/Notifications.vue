<script setup>
// Design_Notifications.md, D-115: SMTP settings + a dedicated recipient
// list (web.app_user.Email is unusable today -- see that doc) + a test
// email, against /api/admin/notifications (NotificationsController).
// D-116: the SMTP account is now a web.credential (picked on the
// Credentials & LDAP admin page, referenced here by SmtpCredentialKey), and
// each notification type can optionally target a role -- additive to the
// flat recipient list below, resolved via that role's own email plus (if
// LDAP is configured) every member of its mapped AD groups.
//
// D-124 Phase 4 (partial conversion): the "Add Recipient" form moved to its
// own routed page (NotificationRecipientCreate.vue,
// admin-notification-recipient-create) -- this page no longer owns that
// inline form. Recipients otherwise still only toggle active/delete in
// place here, untouched, and the SMTP config / notification types / test
// email sections are all untouched too.
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { confirmDelete } from '../../composables/useConfirmDialog'

const router = useRouter()

const config = ref(null)
const configError = ref(null)
const configSaved = ref(false)

const credentials = ref([])
const recipients = ref([])
const recipientsError = ref(null)

const notificationTypes = ref([])
const roles = ref([])
const typesError = ref(null)

const testResult = ref(null)
const testing = ref(false)

const loading = ref(true)
const loadError = ref(null)

async function load() {
  loading.value = true
  loadError.value = null
  try {
    const [configResponse, recipientsResponse, credentialsResponse, typesResponse, rolesResponse] = await Promise.all([
      fetch('/api/admin/notifications/config'),
      fetch('/api/admin/notifications/recipients'),
      fetch('/api/admin/credentials'),
      fetch('/api/admin/notifications/types'),
      fetch('/api/admin/roles')
    ])
    if (!configResponse.ok) throw new Error(`Config request failed: ${configResponse.status}`)
    if (!recipientsResponse.ok) throw new Error(`Recipients request failed: ${recipientsResponse.status}`)
    config.value = await configResponse.json()
    recipients.value = await recipientsResponse.json()
    credentials.value = credentialsResponse.ok ? await credentialsResponse.json() : []
    notificationTypes.value = typesResponse.ok ? await typesResponse.json() : []
    roles.value = rolesResponse.ok ? await rolesResponse.json() : []
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
      smtpCredentialKey: config.value.smtpCredentialKey,
      fromAddress: config.value.fromAddress,
      fromDisplayName: config.value.fromDisplayName,
      ignoreCrlErrors: config.value.ignoreCrlErrors,
      ignoreSslErrors: config.value.ignoreSslErrors
    })
  })
  if (!response.ok) {
    configError.value = `Save failed: ${response.status}`
    return
  }
  configSaved.value = true
  await load()
}

async function setTargetRole(type) {
  typesError.value = null
  const response = await fetch(`/api/admin/notifications/types/${type.notificationTypeKey}/target-role`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ targetRoleKey: type.targetRoleKey })
  })
  if (!response.ok) {
    typesError.value = `Save failed: ${response.status}`
    return
  }
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
  if (!(await confirmDelete(`Delete recipient "${recipient.email}"? This cannot be undone.`))) return
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
          <p>
            <label class="field-label">
              <span class="field-label-text">Credential:</span>
              <select v-model="config.smtpCredentialKey">
                <option :value="null">(none)</option>
                <option v-for="credential in credentials" :key="credential.credentialKey" :value="credential.credentialKey">{{ credential.credentialName }}</option>
              </select>
            </label>
            <small>Managed on the <router-link :to="{ name: 'admin-credentials' }">Credentials & LDAP</router-link> page.</small>
          </p>
        </template>
        <p><label class="field-label"><span class="field-label-text">From Address:</span> <input v-model="config.fromAddress" placeholder="bluetrack@company.com" /></label></p>
        <p><label class="field-label"><span class="field-label-text">From Display Name:</span> <input v-model="config.fromDisplayName" placeholder="BlueTrack" /></label></p>
        <p><label><input v-model="config.ignoreCrlErrors" type="checkbox" /> Ignore CRL issues (skip certificate revocation checking)</label></p>
        <p><label><input v-model="config.ignoreSslErrors" type="checkbox" /> Ignore all SSL errors (skip all certificate validation -- use with caution)</label></p>
        <button type="submit" class="btn-primary">Save</button>
      </form>

      <h3>Notification Types</h3>
      <p>An optional target role for each alert kind -- additive to the recipient list above: the role's own email plus, if LDAP is configured, every member of its mapped AD groups.</p>
      <p v-if="typesError" role="alert">{{ typesError }}</p>
      <table v-if="notificationTypes.length > 0">
        <thead>
          <tr><th>Type</th><th>Description</th><th>Target Role</th></tr>
        </thead>
        <tbody>
          <tr v-for="type in notificationTypes" :key="type.notificationTypeKey">
            <td>{{ type.notificationTypeName }}</td>
            <td>{{ type.description }}</td>
            <td>
              <select v-model="type.targetRoleKey" @change="setTargetRole(type)">
                <option :value="null">(none -- flat list only)</option>
                <option v-for="role in roles" :key="role.appRoleKey" :value="role.appRoleKey">{{ role.roleName }}</option>
              </select>
            </td>
          </tr>
        </tbody>
      </table>

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

      <p><button type="button" class="btn-primary" @click="router.push({ name: 'admin-notification-recipient-create' })">+ Add Recipient</button></p>
    </template>
  </div>
</template>
