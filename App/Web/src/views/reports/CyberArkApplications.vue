<script setup>
// D-282: Reports > CyberArk Applications. GET /api/reports/cyberark-applications (ViewDashboard): the AAM application
// IDs the nightly Self-Hosted Direct pull read from PVWA, with their authentication methods (no hash values) and how
// many accounts and Safes each can retrieve from. Applications deleted in CyberArk are kept, marked, at the end.
import { ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
const items = ref([])
const error = ref(null)
const loading = ref(true)

function hours(app) {
  if (app.accessPermittedFrom == null || app.accessPermittedTo == null) return ''
  if (app.accessPermittedFrom === 0 && app.accessPermittedTo === 24) return t('reports.cyberArkApplications.anyTime')
  return t('reports.cyberArkApplications.hours', { from: app.accessPermittedFrom, to: app.accessPermittedTo })
}

function method(a) {
  const value = a.authValue ?? [a.subject, a.issuer, a.subjectAlternativeName].filter(Boolean).join('; ')
  return value ? `${a.authType}: ${value}` : a.authType
}

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/cyberark-applications')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    items.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div>
    <h2>{{ t('reports.cyberArkApplications.title') }}</h2>
    <p class="hint">{{ t('reports.cyberArkApplications.hint') }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.cyberArkApplications.loadFailed', { error }) }}</p>
    <p v-else-if="items.length === 0">{{ t('reports.cyberArkApplications.empty') }}</p>
    <table v-else>
      <thead>
        <tr>
          <th>{{ t('reports.cyberArkApplications.colAppId') }}</th>
          <th>{{ t('reports.cyberArkApplications.colDescription') }}</th>
          <th>{{ t('reports.cyberArkApplications.colOwner') }}</th>
          <th>{{ t('reports.cyberArkApplications.colStatus') }}</th>
          <th>{{ t('reports.cyberArkApplications.colHours') }}</th>
          <th>{{ t('reports.cyberArkApplications.colMethods') }}</th>
          <th>{{ t('reports.cyberArkApplications.colAccounts') }}</th>
          <th>{{ t('reports.cyberArkApplications.colSafes') }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="app in items" :key="app.cyberArkApplicationKey">
          <td>{{ app.appId }}<br v-if="app.location" /><small v-if="app.location">{{ app.location }}</small></td>
          <td>{{ app.description ?? '' }}</td>
          <td>{{ app.businessOwnerName ?? '' }}<br v-if="app.businessOwnerEmail" /><small v-if="app.businessOwnerEmail">{{ app.businessOwnerEmail }}</small></td>
          <td>
            <template v-if="app.isDeletedInSource">{{ t('reports.cyberArkApplications.deleted') }}</template>
            <template v-else-if="app.disabled">{{ t('reports.cyberArkApplications.disabled') }}</template>
            <template v-else>{{ t('reports.cyberArkApplications.enabled') }}</template>
            <template v-if="app.expirationDate"><br /><small>{{ t('reports.cyberArkApplications.expires', { date: app.expirationDate }) }}</small></template>
          </td>
          <td>{{ hours(app) }}</td>
          <td>
            <span v-if="app.authentications.length === 0">{{ t('reports.cyberArkApplications.noMethods') }}</span>
            <ul v-else class="methods">
              <li v-for="(a, i) in app.authentications" :key="i">{{ method(a) }}</li>
            </ul>
          </td>
          <td>{{ app.accountCount }}</td>
          <td>{{ app.safeCount }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
}
.methods {
  margin: 0;
  padding-left: 1.1rem;
}
</style>
