<script setup>
// D-281/D-282: on an account's page, who can reach its Safe -- directly, through a Vault group, or through an AD group
// whose users the nightly Self-Hosted Direct pull read from AD -- with what they may do, and the CyberArk Applications
// that can retrieve the account. GET /api/account-progress/{accountKey}/safe-access (ViewDashboard). Closed at first,
// as a Safe can have many members; the count is in the summary.
// D-339: direct members are listed one by one; each group that is a member (Vault or AD) is one row with the rights it
// gives, which opens to list its members. A group whose AD members weren't read says so instead of disappearing.
import { ref, computed, onMounted } from 'vue'
import { formatUtcDateTime } from '../utils/formatLocale'
import { useI18n } from 'vue-i18n'
import { formatCount } from '../utils/formatCount'

const props = defineProps({ accountKey: { type: [String, Number], required: true } })
const { t } = useI18n()

const access = ref(null)
const error = ref(null)

const people = computed(() => access.value?.people ?? [])
const groups = computed(() => access.value?.groups ?? [])
const direct = computed(() => people.value.filter(p => p.groupKey === null || p.groupKey === undefined))
const open = ref(new Set())

function membersOf(group) {
  return people.value.filter(p => p.groupKey === group.groupKey)
}

function toggle(group) {
  const next = new Set(open.value)
  if (next.has(group.groupKey)) next.delete(group.groupKey)
  else next.add(group.groupKey)
  open.value = next
}

// "Via AD group: Safe Admins > Team A" shows under its group as "through Team A"; a direct member of the group shows nothing.
function nestedPath(person, group) {
  const rest = person.accessPath.replace(/^Via (AD|Vault) group: /, '')
  return rest.startsWith(group.groupName + ' > ') ? rest.slice(group.groupName.length + 3) : ''
}

// D-360: where the person's user is defined, Vault or LDAP (the API says which; another value is shown as it is).
function userSource(person) {
  if (person.userSource === 'Vault') return t('pages.safeAccess.sourceVault')
  if (person.userSource === 'LDAP') return t('pages.safeAccess.sourceLdap')
  return person.userSource ?? ''
}

function groupKind(group) {
  return group.adReadStatus ? t('pages.safeAccess.adGroup') : t('pages.safeAccess.vaultGroup')
}

// Why a group lists no one: not found in AD, or the read failed (with the members kept from before, if any).
function groupNote(group) {
  if (group.adReadStatus === 'NotFound' && membersOf(group).length === 0) return t('pages.safeAccess.groupNotFound')
  if (group.adReadStatus === 'Failed') return t('pages.safeAccess.groupReadFailed', { message: group.adReadMessage ?? '' })
  return ''
}
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
        <summary>{{ t('pages.safeAccess.membersSummary', { direct: formatCount(direct.length), groups: formatCount(groups.length) }) }}</summary>
        <p v-if="direct.length === 0 && groups.length === 0">{{ t('pages.safeAccess.noPeople') }}</p>
        <table v-else>
          <thead>
            <tr>
              <th>{{ t('pages.safeAccess.colPerson') }}</th>
              <th>{{ t('pages.safeAccess.colName') }}</th>
              <th>{{ t('pages.safeAccess.colSource') }}</th>
              <th>{{ t('pages.safeAccess.colAccess') }}</th>
              <th>{{ t('pages.safeAccess.colUse') }}</th>
              <th>{{ t('pages.safeAccess.colRetrieve') }}</th>
              <th>{{ t('pages.safeAccess.colList') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="person in direct" :key="person.personName + '|' + person.accessPath" class="direct-member">
              <td>{{ person.personName }}<small v-if="!person.isEnabled">{{ ' ' + t('pages.safeAccess.disabled') }}</small></td>
              <td>{{ person.displayName ?? '' }}</td>
              <td class="user-source">{{ userSource(person) }}</td>
              <td>{{ t('pages.safeAccess.direct') }}</td>
              <td>{{ yesNo(person.canUse) }}</td>
              <td>{{ yesNo(person.canRetrieve) }}</td>
              <td>{{ yesNo(person.canList) }}</td>
            </tr>
          </tbody>
          <tbody v-for="group in groups" :key="group.groupKey" class="group">
            <tr class="group-row">
              <td colspan="3">
                <button v-if="membersOf(group).length" type="button" class="group-toggle" :aria-expanded="open.has(group.groupKey)" @click="toggle(group)">
                  <span aria-hidden="true">{{ open.has(group.groupKey) ? '▾' : '▸' }}</span> {{ group.groupName }}
                </button>
                <strong v-else>{{ group.groupName }}</strong>
                <small>{{ ' ' + (membersOf(group).length ? t('pages.safeAccess.groupMembers', { count: formatCount(membersOf(group).length) }, membersOf(group).length) : t('pages.safeAccess.groupNoMembers')) }}</small>
                <template v-if="groupNote(group)"><br /><small class="group-note">{{ groupNote(group) }}</small></template>
              </td>
              <td>{{ groupKind(group) }}</td>
              <td>{{ yesNo(group.canUse) }}</td>
              <td>{{ yesNo(group.canRetrieve) }}</td>
              <td>{{ yesNo(group.canList) }}</td>
            </tr>
            <template v-if="open.has(group.groupKey)">
              <tr v-for="person in membersOf(group)" :key="person.personName + '|' + person.accessPath" class="group-member">
                <td>{{ person.personName }}<small v-if="!person.isEnabled">{{ ' ' + t('pages.safeAccess.disabled') }}</small></td>
                <td>{{ person.displayName ?? '' }}</td>
              <td class="user-source">{{ userSource(person) }}</td>
                <td>{{ nestedPath(person, group) ? t('pages.safeAccess.through', { path: nestedPath(person, group) }) : '' }}</td>
                <td>{{ yesNo(person.canUse) }}</td>
                <td>{{ yesNo(person.canRetrieve) }}</td>
                <td>{{ yesNo(person.canList) }}</td>
              </tr>
            </template>
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
.group-toggle {
  background: none;
  border: none;
  padding: 0;
  font: inherit;
  font-weight: bold;
  color: var(--color-link);
  cursor: pointer;
}
.group-member td:first-child {
  padding-left: 1.75rem;
}
.group-note {
  color: var(--color-error-text);
}
</style>
