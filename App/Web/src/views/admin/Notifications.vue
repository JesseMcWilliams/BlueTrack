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
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const router = useRouter()

const config = ref(null)
// UI review W9-3: the SMTP settings as last loaded or saved; Send test email uses those, never unsaved form edits.
const configSnapshot = ref('')
const configDirty = computed(() => !!config.value && JSON.stringify(config.value) !== configSnapshot.value)
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
// The option labels; the stored values above stay English.
const DAY_KEYS = {
  Mon: 'admin.notifications.dayMon',
  Tue: 'admin.notifications.dayTue',
  Wed: 'admin.notifications.dayWed',
  Thu: 'admin.notifications.dayThu',
  Fri: 'admin.notifications.dayFri',
  Sat: 'admin.notifications.daySat',
  Sun: 'admin.notifications.daySun'
}

const oauthMethods = ['Microsoft365', 'GoogleWorkspace', 'GenericOAuth2']
const isOAuth = computed(() => oauthMethods.includes(config.value?.authMethod))
const usesCredential = computed(() => config.value?.authMethod === 'Basic' || isOAuth.value)
const credentialLabel = computed(() => {
  switch (config.value?.authMethod) {
    case 'Microsoft365':
      return config.value.oAuthClientCredentialType === 'Certificate' ? t('admin.notifications.certificateCredential') : t('admin.notifications.clientSecretCredential')
    case 'GoogleWorkspace':
      return t('admin.notifications.serviceAccountCredential')
    case 'GenericOAuth2':
      return t('admin.notifications.clientSecretCredential')
    default:
      return t('admin.notifications.credential')
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
    if (!configResponse.ok) throw new Error(t('admin.notifications.configRequestFailed', { status: configResponse.status }))
    if (!recipientsResponse.ok) throw new Error(t('admin.notifications.recipientsRequestFailed', { status: recipientsResponse.status }))
    config.value = await configResponse.json()
    configSnapshot.value = JSON.stringify(config.value)
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

// UI review W9-1: an action reloads only the part of the page it changed. Reloading everything (and showing
// "Loading") remounted the page and threw away whatever had been typed into the other sections.
async function loadConfig() {
  const response = await fetch('/api/admin/notifications/config')
  if (!response.ok) return
  config.value = await response.json()
  configSnapshot.value = JSON.stringify(config.value)
}

async function loadTypes() {
  const response = await fetch('/api/admin/notifications/types')
  if (response.ok) notificationTypes.value = await response.json()
}

async function loadRecipients() {
  const response = await fetch('/api/admin/notifications/recipients')
  if (response.ok) recipients.value = await response.json()
}

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
    configError.value = detail ? t('admin.notifications.saveFailedDetail', { status: response.status, detail }) : t('common.saveFailed', { status: response.status })
    return
  }
  configSaved.value = true
  await loadConfig()
}

async function setTargetRole(type) {
  typesError.value = null
  const response = await fetch(`/api/admin/notifications/types/${type.notificationTypeKey}/target-role`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ targetRoleKey: type.targetRoleKey })
  })
  if (!response.ok) {
    // UI review W9-6: the select already shows the role that was picked; reload so it shows what is saved.
    const message = t('common.saveFailed', { status: response.status })
    await loadTypes()
    typesError.value = message
    return
  }
  await loadTypes()
}

async function setTypeEnabled(type, isEnabled) {
  typesError.value = null
  const response = await fetch(`/api/admin/notifications/types/${type.notificationTypeKey}/enabled`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ isEnabled })
  })
  if (!response.ok) {
    typesError.value = t('common.saveFailed', { status: response.status })
    return
  }
  await loadTypes()
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
    scheduleError.value = detail ? t('admin.notifications.saveFailedDetail', { status: response.status, detail }) : t('common.saveFailed', { status: response.status })
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
    recipientsError.value = t('admin.notifications.updateFailed', { status: response.status })
    return
  }
  await loadRecipients()
}

async function removeRecipient(recipient) {
  if (!(await confirmDelete(t('admin.notifications.confirmDelete', { email: recipient.email })))) return
  recipientsError.value = null
  const response = await fetch(`/api/admin/notifications/recipients/${recipient.recipientKey}`, { method: 'DELETE' })
  if (!response.ok) {
    recipientsError.value = t('common.deleteFailed', { status: response.status })
    return
  }
  await loadRecipients()
}

async function sendTestEmail() {
  testing.value = true
  testResult.value = null
  try {
    const response = await fetch('/api/admin/notifications/test', { method: 'POST' })
    testResult.value = response.ok ? await response.json() : { success: false, error: t('common.requestFailed', { status: response.status }) }
  } finally {
    testing.value = false
  }
}
</script>

<template>
  <div>
    <h2>{{ t('admin.notifications.title') }}</h2>
    <p>{{ t('admin.notifications.intro') }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <template v-else>
      <h3>{{ t('admin.notifications.smtpHeading') }}</h3>
      <p v-if="configError" role="alert">{{ configError }}</p>
      <p v-if="configSaved" role="status">{{ t('common.saved') }}</p>
      <form @submit.prevent="saveConfig">
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.notifications.smtpHost') }}</span> <input v-model="config.smtpHost" :placeholder="t('admin.notifications.smtpHostPlaceholder')" /></label></p>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.notifications.smtpPort') }}</span> <input v-model.number="config.smtpPort" type="number" /></label></p>
        <p><label><input v-model="config.enableStartTls" type="checkbox" /> {{ t('admin.notifications.enableStartTls') }}</label></p>
        <p>
          <label class="field-label">
            <span class="field-label-text">{{ t('admin.notifications.authMethod') }}</span>
            <select v-model="config.authMethod" @change="onAuthMethodChange">
              <option value="None">{{ t('admin.notifications.authNone') }}</option>
              <option value="Basic">{{ t('admin.notifications.authBasic') }}</option>
              <option value="Microsoft365">{{ t('admin.notifications.authMicrosoft365') }}</option>
              <option value="GoogleWorkspace">{{ t('admin.notifications.authGoogle') }}</option>
              <option value="GenericOAuth2">{{ t('admin.notifications.authGeneric') }}</option>
            </select>
          </label>
        </p>
        <p v-if="config.authMethod === 'Microsoft365'" id="smtp-auth-help">
          {{ t('admin.notifications.helpMicrosoft365') }}
        </p>
        <p v-else-if="config.authMethod === 'GoogleWorkspace'" id="smtp-auth-help">
          {{ t('admin.notifications.helpGoogle') }}
        </p>
        <p v-else-if="config.authMethod === 'GenericOAuth2'" id="smtp-auth-help">
          {{ t('admin.notifications.helpGeneric') }}
        </p>
        <template v-if="config.authMethod === 'Microsoft365'">
          <p><label class="field-label"><span class="field-label-text">{{ t('admin.notifications.tenantId') }}</span> <input v-model.trim="config.oAuthTenantId" required :placeholder="t('admin.notifications.tenantIdPlaceholder')" /></label></p>
          <p><label class="field-label"><span class="field-label-text">{{ t('admin.notifications.applicationId') }}</span> <input v-model.trim="config.oAuthClientId" required /></label></p>
          <p>
            <label class="field-label">
              <span class="field-label-text">{{ t('admin.notifications.clientCredentialType') }}</span>
              <select v-model="config.oAuthClientCredentialType">
                <option value="Secret">{{ t('admin.notifications.credentialTypeSecret') }}</option>
                <option value="Certificate">{{ t('admin.notifications.credentialTypeCertificate') }}</option>
              </select>
            </label>
          </p>
        </template>
        <template v-if="config.authMethod === 'GenericOAuth2'">
          <p><label class="field-label"><span class="field-label-text">{{ t('admin.notifications.tokenUrl') }}</span> <input v-model.trim="config.oAuthTokenUrl" type="url" required :placeholder="t('admin.notifications.tokenUrlPlaceholder')" /></label></p>
          <p><label class="field-label"><span class="field-label-text">{{ t('admin.notifications.clientId') }}</span> <input v-model.trim="config.oAuthClientId" required /></label></p>
          <p>
            <label class="field-label">
              <span class="field-label-text">{{ t('admin.notifications.clientAuthentication') }}</span>
              <select v-model="config.oAuthClientAuthStyle">
                <option value="Basic">{{ t('admin.notifications.clientAuthBasic') }}</option>
                <option value="Body">{{ t('admin.notifications.clientAuthBody') }}</option>
              </select>
            </label>
          </p>
          <p><label class="field-label"><span class="field-label-text">{{ t('admin.notifications.scope') }}</span> <input v-model.trim="config.oAuthScope" :placeholder="t('admin.notifications.scopePlaceholder')" /></label></p>
          <p><label class="field-label"><span class="field-label-text">{{ t('admin.notifications.audience') }}</span> <input v-model.trim="config.oAuthAudience" /></label></p>
        </template>
        <template v-if="usesCredential">
          <p>
            <label class="field-label">
              <span class="field-label-text">{{ credentialLabel }}</span>
              <select v-model="config.smtpCredentialKey" :required="isOAuth" :aria-describedby="isOAuth ? 'smtp-auth-help' : undefined">
                <option :value="null">{{ t('common.none') }}</option>
                <option v-for="credential in credentials" :key="credential.credentialKey" :value="credential.credentialKey">{{ credential.credentialName }}</option>
              </select>
            </label>
            <i18n-t keypath="admin.notifications.managedOn" tag="small" scope="global">
              <template #link><router-link :to="{ name: 'admin-credentials' }">{{ t('admin.notifications.credentialsLink') }}</router-link></template>
            </i18n-t>
          </p>
        </template>
        <p v-if="isOAuth">
          <label class="field-label">
            <span class="field-label-text">{{ config.authMethod === 'GenericOAuth2' ? t('admin.notifications.xoauth2Username') : t('admin.notifications.signInMailbox') }}</span>
            <input v-model.trim="config.oAuthUsername" :placeholder="t('admin.notifications.usernamePlaceholder')" />
          </label>
        </p>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.notifications.fromAddress') }}</span> <input v-model="config.fromAddress" :placeholder="t('admin.notifications.fromAddressPlaceholder')" /></label></p>
        <p><label class="field-label"><span class="field-label-text">{{ t('admin.notifications.fromDisplayName') }}</span> <input v-model="config.fromDisplayName" :placeholder="t('admin.notifications.fromDisplayNamePlaceholder')" /></label></p>
        <p><label><input v-model="config.ignoreCrlErrors" type="checkbox" /> {{ t('admin.notifications.ignoreCrl') }}</label></p>
        <p><label><input v-model="config.ignoreSslErrors" type="checkbox" /> {{ t('admin.notifications.ignoreSsl') }}</label></p>
        <button type="submit" class="btn-primary">{{ t('common.save') }}</button>
      </form>

      <h3>{{ t('admin.notifications.typesHeading') }}</h3>
      <p>{{ t('admin.notifications.typesIntro') }}</p>
      <p v-if="typesError" role="alert">{{ typesError }}</p>
      <table v-if="notificationTypes.length > 0">
        <thead>
          <tr><th>{{ t('admin.notifications.type') }}</th><th>{{ t('common.description') }}</th><th>{{ t('common.enabled') }}</th><th>{{ t('admin.notifications.targetRole') }}</th></tr>
        </thead>
        <tbody>
          <tr v-for="type in notificationTypes" :key="type.notificationTypeKey">
            <td>{{ type.notificationTypeName }}</td>
            <td>{{ type.description }}</td>
            <td>
              <input
                type="checkbox"
                :checked="type.isEnabled"
                :aria-label="t('admin.notifications.typeEnabledLabel', { name: type.notificationTypeName })"
                @change="setTypeEnabled(type, $event.target.checked)"
              />
            </td>
            <td>
              <select v-model="type.targetRoleKey" @change="setTargetRole(type)">
                <option :value="null">{{ t('admin.notifications.noTargetRole') }}</option>
                <option v-for="role in roles" :key="role.appRoleKey" :value="role.appRoleKey">{{ role.roleName }}</option>
              </select>
            </td>
          </tr>
        </tbody>
      </table>

      <template v-if="schedule">
        <h3>{{ t('admin.notifications.scheduleHeading') }}</h3>
        <p>{{ t('admin.notifications.scheduleIntro') }}</p>
        <p v-if="scheduleError" role="alert">{{ scheduleError }}</p>
        <p v-if="scheduleSaved" role="status">{{ t('admin.notifications.scheduleSaved') }}</p>
        <form @submit.prevent="saveSchedule">
          <p><label class="field-label"><span class="field-label-text">{{ t('admin.notifications.dailyDigestTime') }}</span> <input v-model="schedule.dailyDigestTime" type="time" required /></label></p>
          <p>
            <label class="field-label">
              <span class="field-label-text">{{ t('admin.notifications.weeklySummaryDay') }}</span>
              <select v-model="schedule.weeklySummaryDay">
                <option v-for="day in weekDays" :key="day" :value="day">{{ t(DAY_KEYS[day]) }}</option>
              </select>
            </label>
          </p>
          <p><label class="field-label"><span class="field-label-text">{{ t('admin.notifications.weeklySummaryTime') }}</span> <input v-model="schedule.weeklySummaryTime" type="time" required /></label></p>
          <button type="submit" class="btn-primary">{{ t('admin.notifications.saveSchedule') }}</button>
        </form>
      </template>

      <h3>{{ t('admin.notifications.testHeading') }}</h3>
      <p>{{ t('admin.notifications.testIntro') }}</p>
      <button :disabled="testing || configDirty" @click="sendTestEmail">{{ t('admin.notifications.sendTestEmail') }}</button>
      <small v-if="configDirty">{{ ' ' + t('admin.notifications.testNeedsSave') }}</small>
      <p v-if="testResult?.success" role="status">{{ t('admin.notifications.sentTo', { count: formatCount(testResult.recipientCount) }) }}</p>
      <p v-else-if="testResult" role="alert">{{ t('admin.notifications.failed', { error: testResult.error }) }}</p>

      <h3>{{ t('admin.notifications.recipientsHeading') }}</h3>
      <p v-if="recipientsError" role="alert">{{ recipientsError }}</p>
      <table v-if="recipients.length > 0">
        <thead>
          <tr><th>{{ t('admin.notifications.email') }}</th><th>{{ t('admin.notifications.displayName') }}</th><th>{{ t('common.active') }}</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="recipient in recipients" :key="recipient.recipientKey">
            <td>{{ recipient.email }}</td>
            <td>{{ recipient.displayName }}</td>
            <td>{{ recipient.isActive ? t('admin.notifications.yes') : t('admin.notifications.no') }}</td>
            <td>
              <button type="button" @click="toggleRecipientActive(recipient)">{{ recipient.isActive ? t('admin.notifications.deactivate') : t('admin.notifications.activate') }}</button>
              <button type="button" @click="removeRecipient(recipient)">{{ t('common.delete') }}</button>
            </td>
          </tr>
        </tbody>
      </table>
      <p v-else>{{ t('admin.notifications.noRecipients') }}</p>

      <p><button type="button" class="btn-primary" @click="router.push({ name: 'admin-notification-recipient-create' })">{{ t('admin.notifications.addRecipient') }}</button></p>
    </template>
  </div>
</template>
