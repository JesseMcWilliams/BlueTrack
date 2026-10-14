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
//
// D-222: each notification type can be turned on or off (the three new
// ones -- NightlyImportFailed, OverdueRiskExceptions, WeeklySummary -- start
// off), and a Schedule section sets the daily digest time and the weekly
// summary's day and time (server local time), saved by its own PUT.
//
// D-223: the Auth Method adds Microsoft 365, Google Workspace and Generic
// OAuth2 (XOAUTH2). Only the selected mode's fields are shown; the secret
// itself (client secret, PEM certificate and key, or the service account's
// JSON key) is a credential picked like Basic's, never typed here.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { formatCount } from '../../utils/formatCount'

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

const schedule = ref(null)
const scheduleError = ref(null)
const scheduleSaved = ref(false)
const weekDays = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']

const oauthMethods = ['Microsoft365', 'GoogleWorkspace', 'GenericOAuth2']
const isOAuth = computed(() => oauthMethods.includes(config.value?.authMethod))
const usesCredential = computed(() => config.value?.authMethod === 'Basic' || isOAuth.value)
const credentialLabel = computed(() => {
  switch (config.value?.authMethod) {
    case 'Microsoft365':
      return config.value.oAuthClientCredentialType === 'Certificate' ? 'Certificate and key credential:' : 'Client secret credential:'
    case 'GoogleWorkspace':
      return 'Service account key credential:'
    case 'GenericOAuth2':
      return 'Client secret credential:'
    default:
      return 'Credential:'
  }
})

// Defaults when a mode is first picked; nothing already entered is overwritten.
function onAuthMethodChange() {
  const c = config.value
  if (c.authMethod === 'Microsoft365') {
    c.oAuthClientCredentialType ||= 'Secret'
    c.smtpHost ||= 'smtp.office365.com'
  } else if (c.authMethod === 'GoogleWorkspace') {
    c.smtpHost ||= 'smtp.gmail.com'
  } else if (c.authMethod === 'GenericOAuth2') {
    c.oAuthClientAuthStyle ||= 'Basic'
  }
  if (isOAuth.value) {
    c.enableStartTls = true
    c.smtpPort ||= 587
  }
}

// Only the selected mode's OAuth2 fields are saved; the others are cleared.
function oauthFieldsFor(c) {
  const m365 = c.authMethod === 'Microsoft365'
  const generic = c.authMethod === 'GenericOAuth2'
  const value = (keep, v) => (keep && v ? v : null)
  return {
    oAuthTenantId: value(m365, c.oAuthTenantId),
    oAuthClientId: value(m365 || generic, c.oAuthClientId),
    oAuthClientCredentialType: value(m365, c.oAuthClientCredentialType),
    oAuthTokenUrl: value(generic, c.oAuthTokenUrl),
    oAuthScope: value(generic, c.oAuthScope),
    oAuthAudience: value(generic, c.oAuthAudience),
    oAuthClientAuthStyle: value(generic, c.oAuthClientAuthStyle),
    oAuthUsername: value(oauthMethods.includes(c.authMethod), c.oAuthUsername)
  }
}

const testResult = ref(null)
const testing = ref(false)

const loading = ref(true)
const loadError = ref(null)

async function load() {
  loading.value = true
  loadError.value = null
  try {
    const [configResponse, recipientsResponse, credentialsResponse, typesResponse, rolesResponse, scheduleResponse] = await Promise.all([
      fetch('/api/admin/notifications/config'),
      fetch('/api/admin/notifications/recipients'),
      fetch('/api/admin/credentials'),
      fetch('/api/admin/notifications/types'),
      fetch('/api/admin/roles'),
      fetch('/api/admin/notifications/schedule')
    ])
    if (!configResponse.ok) throw new Error(`Config request failed: ${configResponse.status}`)
    if (!recipientsResponse.ok) throw new Error(`Recipients request failed: ${recipientsResponse.status}`)
    config.value = await configResponse.json()
    recipients.value = await recipientsResponse.json()
    credentials.value = credentialsResponse.ok ? await credentialsResponse.json() : []
    notificationTypes.value = typesResponse.ok ? await typesResponse.json() : []
    roles.value = rolesResponse.ok ? await rolesResponse.json() : []
    schedule.value = scheduleResponse.ok ? await scheduleResponse.json() : null
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
      ignoreSslErrors: config.value.ignoreSslErrors,
      ...oauthFieldsFor(config.value)
    })
  })
  if (!response.ok) {
    let detail = ''
    try { detail = (await response.json())?.detail ?? '' } catch { /* not JSON */ }
    configError.value = `Save failed: ${response.status}${detail ? ` -- ${detail}` : ''}`
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

async function setTypeEnabled(type, isEnabled) {
  typesError.value = null
  const response = await fetch(`/api/admin/notifications/types/${type.notificationTypeKey}/enabled`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ isEnabled })
  })
  if (!response.ok) {
    typesError.value = `Save failed: ${response.status}`
    return
  }
  await load()
}

async function saveSchedule() {
  scheduleError.value = null
  scheduleSaved.value = false
  const response = await fetch('/api/admin/notifications/schedule', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      dailyDigestTime: schedule.value.dailyDigestTime,
      weeklySummaryDay: schedule.value.weeklySummaryDay,
      weeklySummaryTime: schedule.value.weeklySummaryTime
    })
  })
  if (!response.ok) {
    let detail = ''
    try { detail = (await response.json())?.detail ?? '' } catch { /* not JSON */ }
    scheduleError.value = `Save failed: ${response.status}${detail ? ` -- ${detail}` : ''}`
    return
  }
  scheduleSaved.value = true
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
    <p>SMTP settings, the recipient list and the schedule for BlueTrack's automated alerts (e.g. a failed nightly Import and Load, overdue Risk Exceptions, the weekly summary).</p>
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
            <select v-model="config.authMethod" @change="onAuthMethodChange">
              <option value="None">None (open relay)</option>
              <option value="Basic">Basic (username/password)</option>
              <option value="Microsoft365">Microsoft 365 (OAuth2)</option>
              <option value="GoogleWorkspace">Google Workspace (OAuth2)</option>
              <option value="GenericOAuth2">Generic OAuth2 (client credentials)</option>
            </select>
          </label>
        </p>
        <p v-if="config.authMethod === 'Microsoft365'" id="smtp-auth-help">
          App-only sign-in to Exchange Online: server smtp.office365.com, port 587, STARTTLS. The app needs the SMTP.SendAsApp
          permission with admin consent, a service principal registered in Exchange Online with access to the mailbox, and
          SMTP AUTH turned on for the mailbox.
        </p>
        <p v-else-if="config.authMethod === 'GoogleWorkspace'" id="smtp-auth-help">
          A service account with domain-wide delegation for https://mail.google.com/ signs in as the mailbox: server
          smtp.gmail.com, port 587, STARTTLS. Store the service account's whole JSON key file as the credential's password.
        </p>
        <p v-else-if="config.authMethod === 'GenericOAuth2'" id="smtp-auth-help">
          A client credentials token from your OAuth2 provider, sent to the SMTP server as XOAUTH2. The token URL must use https.
        </p>
        <template v-if="config.authMethod === 'Microsoft365'">
          <p><label class="field-label"><span class="field-label-text">Tenant ID:</span> <input v-model.trim="config.oAuthTenantId" required placeholder="contoso.onmicrosoft.com or GUID" /></label></p>
          <p><label class="field-label"><span class="field-label-text">Client (application) ID:</span> <input v-model.trim="config.oAuthClientId" required /></label></p>
          <p>
            <label class="field-label">
              <span class="field-label-text">Client credential type:</span>
              <select v-model="config.oAuthClientCredentialType">
                <option value="Secret">Client secret</option>
                <option value="Certificate">Certificate (PEM certificate and private key)</option>
              </select>
            </label>
          </p>
        </template>
        <template v-if="config.authMethod === 'GenericOAuth2'">
          <p><label class="field-label"><span class="field-label-text">Token URL:</span> <input v-model.trim="config.oAuthTokenUrl" type="url" required placeholder="https://idp.company.com/oauth2/token" /></label></p>
          <p><label class="field-label"><span class="field-label-text">Client ID:</span> <input v-model.trim="config.oAuthClientId" required /></label></p>
          <p>
            <label class="field-label">
              <span class="field-label-text">Client authentication:</span>
              <select v-model="config.oAuthClientAuthStyle">
                <option value="Basic">Authorization header (HTTP Basic)</option>
                <option value="Body">Form fields (client_id and client_secret)</option>
              </select>
            </label>
          </p>
          <p><label class="field-label"><span class="field-label-text">Scope (optional):</span> <input v-model.trim="config.oAuthScope" placeholder="space-separated" /></label></p>
          <p><label class="field-label"><span class="field-label-text">Audience (optional):</span> <input v-model.trim="config.oAuthAudience" /></label></p>
        </template>
        <template v-if="usesCredential">
          <p>
            <label class="field-label">
              <span class="field-label-text">{{ credentialLabel }}</span>
              <select v-model="config.smtpCredentialKey" :required="isOAuth" :aria-describedby="isOAuth ? 'smtp-auth-help' : undefined">
                <option :value="null">(none)</option>
                <option v-for="credential in credentials" :key="credential.credentialKey" :value="credential.credentialKey">{{ credential.credentialName }}</option>
              </select>
            </label>
            <small>Managed on the <router-link :to="{ name: 'admin-credentials' }">Credentials & LDAP</router-link> page.</small>
          </p>
        </template>
        <p v-if="isOAuth">
          <label class="field-label">
            <span class="field-label-text">{{ config.authMethod === 'GenericOAuth2' ? 'XOAUTH2 username:' : 'Sign-in mailbox:' }}</span>
            <input v-model.trim="config.oAuthUsername" placeholder="blank = the From address" />
          </label>
        </p>
        <p><label class="field-label"><span class="field-label-text">From Address:</span> <input v-model="config.fromAddress" placeholder="bluetrack@company.com" /></label></p>
        <p><label class="field-label"><span class="field-label-text">From Display Name:</span> <input v-model="config.fromDisplayName" placeholder="BlueTrack" /></label></p>
        <p><label><input v-model="config.ignoreCrlErrors" type="checkbox" /> Ignore CRL issues (skip certificate revocation checking)</label></p>
        <p><label><input v-model="config.ignoreSslErrors" type="checkbox" /> Ignore all SSL errors (skip all certificate validation -- use with caution)</label></p>
        <button type="submit" class="btn-primary">Save</button>
      </form>

      <h3>Notification Types</h3>
      <p>Turn each alert kind on or off, and optionally pick a target role -- additive to the recipient list below: the role's own email plus, if LDAP is configured, every member of its mapped AD groups.</p>
      <p v-if="typesError" role="alert">{{ typesError }}</p>
      <table v-if="notificationTypes.length > 0">
        <thead>
          <tr><th>Type</th><th>Description</th><th>Enabled</th><th>Target Role</th></tr>
        </thead>
        <tbody>
          <tr v-for="type in notificationTypes" :key="type.notificationTypeKey">
            <td>{{ type.notificationTypeName }}</td>
            <td>{{ type.description }}</td>
            <td>
              <input
                type="checkbox"
                :checked="type.isEnabled"
                :aria-label="`${type.notificationTypeName} enabled`"
                @change="setTypeEnabled(type, $event.target.checked)"
              />
            </td>
            <td>
              <select v-model="type.targetRoleKey" @change="setTargetRole(type)">
                <option :value="null">(none -- flat list only)</option>
                <option v-for="role in roles" :key="role.appRoleKey" :value="role.appRoleKey">{{ role.roleName }}</option>
              </select>
            </td>
          </tr>
        </tbody>
      </table>

      <template v-if="schedule">
        <h3>Schedule</h3>
        <p>When the scheduled emails go out, in the server's local time. Each goes out once: the daily digest once a day, the weekly summary once a week.</p>
        <p v-if="scheduleError" role="alert">{{ scheduleError }}</p>
        <p v-if="scheduleSaved" role="status">Schedule saved.</p>
        <form @submit.prevent="saveSchedule">
          <p><label class="field-label"><span class="field-label-text">Daily digest time:</span> <input v-model="schedule.dailyDigestTime" type="time" required /></label></p>
          <p>
            <label class="field-label">
              <span class="field-label-text">Weekly summary day:</span>
              <select v-model="schedule.weeklySummaryDay">
                <option v-for="day in weekDays" :key="day" :value="day">{{ day }}</option>
              </select>
            </label>
          </p>
          <p><label class="field-label"><span class="field-label-text">Weekly summary time:</span> <input v-model="schedule.weeklySummaryTime" type="time" required /></label></p>
          <button type="submit" class="btn-primary">Save Schedule</button>
        </form>
      </template>

      <h3>Test Connection</h3>
      <p>Sends a real test email to every active recipient below, using the saved SMTP configuration.</p>
      <button :disabled="testing" @click="sendTestEmail">Send Test Email</button>
      <p v-if="testResult?.success" role="status">Sent successfully to {{ formatCount(testResult.recipientCount) }} recipient(s).</p>
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
