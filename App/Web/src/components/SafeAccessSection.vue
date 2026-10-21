<script setup>
// D-281/D-282: on an account's page, who can reach its Safe -- directly, through a Vault group, or through an AD group
// whose users the nightly Self-Hosted Direct pull read from AD -- with what they may do, and the CyberArk Applications
// that can retrieve the account. GET /api/account-progress/{accountKey}/safe-access (ViewDashboard). Closed at first,
// as a Safe can have many members; the count is in the summary.
import { ref, computed, onMounted } from 'vue'
import { formatUtcDateTime } from '../utils/formatLocale'
import { useI18n } from 'vue-i18n'

const props = defineProps({ accountKey: { type: [String, Number], required: true } })
const { t } = useI18n()

const access = ref(null)
const error = ref(null)

const people = computed(() => access.value?.people ?? [])
const applications = computed(() => access.value?.applications ?? [])

const yesNo = value => (value ? t('common.yes') : t('common.no'))

onMounted(async () => {
  try {
    const response = await fetch(`/api/account-progress/${props.accountKey}/safe-access`)
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    access.value = await response.json()
  } catch (err) {
    error.value = err.message
  }
})
</script>

<template>
  <section class="safe-access" aria-labelledby="safe-access-heading">
    <h2 id="safe-access-heading">{{ t('pages.safeAccess.title') }}</h2>
    <p v-if="error" role="alert">{{ t('pages.safeAccess.loadFailed', { error }) }}</p>
    <p v-else-if="!access" role="status">{{ t('common.loading') }}</p>
    <template v-else>
      <details>
        <summary>{{ t('pages.safeAccess.peopleSummary', { count: people.length }, people.length) }}</summary>
        <p v-if="people.length === 0">{{ t('pages.safeAccess.noPeople') }}</p>
        <table v-else>
          <thead>
            <tr>
              <th>{{ t('pages.safeAccess.colPerson') }}</th>
              <th>{{ t('pages.safeAccess.colName') }}</th>
              <th>{{ t('pages.safeAccess.colAccess') }}</th>
              <th>{{ t('pages.safeAccess.colUse') }}</th>
              <th>{{ t('pages.safeAccess.colRetrieve') }}</th>
              <th>{{ t('pages.safeAccess.colList') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="person in people" :key="person.personName + '|' + person.accessPath">
              <td>{{ person.personName }}<small v-if="!person.isEnabled">{{ ' ' + t('pages.safeAccess.disabled') }}</small></td>
              <td>{{ person.displayName ?? '' }}</td>
              <td>{{ person.accessPath }}</td>
              <td>{{ yesNo(person.canUse) }}</td>
              <td>{{ yesNo(person.canRetrieve) }}</td>
              <td>{{ yesNo(person.canList) }}</td>
            </tr>
          </tbody>
        </table>
        <p v-if="access.adReadAt" class="hint">{{ t('pages.safeAccess.adReadAt', { time: formatUtcDateTime(access.adReadAt) }) }}</p>
      </details>
      <h3>{{ t('pages.safeAccess.applicationsTitle') }}</h3>
      <p v-if="applications.length === 0">{{ t('pages.safeAccess.noApplications') }}</p>
      <ul v-else>
        <li v-for="app in applications" :key="app.cyberArkApplicationKey + '|' + app.accessPath">
          <router-link :to="{ name: 'reports-cyberark-applications' }">{{ app.appId }}</router-link>
          <template v-if="app.description"> ({{ app.description }})</template>
          — {{ app.accessPath }}<template v-if="app.disabled"> · {{ t('pages.safeAccess.applicationDisabled') }}</template>
        </li>
      </ul>
    </template>
  </section>
</template>

<style scoped>
.safe-access {
  margin-top: var(--space-4, 1.5rem);
}
.hint {
  max-width: 60em;
}
</style>
