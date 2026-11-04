<script setup>
// D-335: Reports > Safe Administrators, the least-privilege view. GET /api/reports/safe-administrators (ViewDashboard):
// one row per Safe and person who holds Manage Safe or Manage Safe Members there, directly or through a Vault or AD
// group, and whether they hold every Safe permission ("Full rights"). The page filters the rows itself: CyberArk's
// built-in users are hidden unless asked for (built-in groups and their people are shown, the user's choice), and By person adds up each person's Safes.
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatCount } from '../../utils/formatCount'
import ReportExportButtons from '../../components/ReportExportButtons.vue'

const { t } = useI18n()
const rows = ref([])
const error = ref(null)
const loading = ref(true)
const view = ref('safe')
const includeBuiltIn = ref(false)
const onlyFullRights = ref(false)
const search = ref('')

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/safe-administrators')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    rows.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
})

const shown = computed(() => {
  const term = search.value.trim().toLowerCase()
  return rows.value.filter(r =>
    (includeBuiltIn.value || !r.isBuiltIn) &&
    (!onlyFullRights.value || r.fullRights) &&
    (!term || r.safeName.toLowerCase().includes(term) || r.personName.toLowerCase().includes(term) || (r.displayName ?? '').toLowerCase().includes(term)))
})

// As SafeAdministratorsRepository.ByPerson, so the page and the export agree.
const people = computed(() => {
  const byName = new Map()
  for (const r of shown.value) {
    const key = r.personName.toLowerCase()
    const p = byName.get(key) ?? { personName: r.personName, displayName: null, isEnabled: false, rows: [] }
    p.displayName ??= r.displayName || null
    p.isEnabled ||= r.isEnabled
    p.rows.push(r)
    byName.set(key, p)
  }
  return [...byName.values()].map(p => ({
    personName: p.personName,
    displayName: p.displayName,
    isEnabled: p.isEnabled,
    isBuiltIn: p.rows.every(r => r.isBuiltIn),
    safes: p.rows.length,
    manageSafeSafes: p.rows.filter(r => r.manageSafe).length,
    manageSafeMembersSafes: p.rows.filter(r => r.manageSafeMembers).length,
    fullRightsSafes: p.rows.filter(r => r.fullRights).length
  })).sort((a, b) => b.fullRightsSafes - a.fullRightsSafes || b.safes - a.safes || a.personName.localeCompare(b.personName))
})

const yesNo = value => value ? t('common.yes') : t('common.no')
const exportParams = computed(() => ({ view: view.value === 'person' ? 'person' : 'safe', includeBuiltIn: includeBuiltIn.value ? 'true' : '' }))
const hiddenBuiltIn = computed(() => includeBuiltIn.value ? 0 : rows.value.filter(r => r.isBuiltIn).length)
</script>

<template>
  <div>
    <h2>{{ t('reports.safeAdministrators.title') }}</h2>
    <p class="hint">{{ t('reports.safeAdministrators.hint') }}</p>
    <div class="list-toolbar">
      <label class="field-label">
        <span class="field-label-text">{{ t('reports.safeAdministrators.view') }}</span>
        <select v-model="view">
          <option value="safe">{{ t('reports.safeAdministrators.bySafe') }}</option>
          <option value="person">{{ t('reports.safeAdministrators.byPerson') }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">{{ t('reports.safeAdministrators.search') }}</span> <input v-model="search" type="search" /></label>
      <label><input v-model="onlyFullRights" type="checkbox" /> {{ t('reports.safeAdministrators.onlyFullRights') }}</label>
      <label><input v-model="includeBuiltIn" type="checkbox" /> {{ t('reports.safeAdministrators.includeBuiltIn') }}</label>
    </div>
    <ReportExportButtons path="/api/reports/safe-administrators/export" :params="exportParams" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.safeAdministrators.loadFailed', { error }) }}</p>
    <template v-else>
      <p v-if="hiddenBuiltIn > 0" class="hint">{{ t('reports.safeAdministrators.builtInHidden', { count: formatCount(hiddenBuiltIn) }, hiddenBuiltIn) }}</p>
      <p v-if="shown.length === 0">{{ t('reports.safeAdministrators.empty') }}</p>
      <table v-else-if="view === 'safe'">
        <thead>
          <tr>
            <th>{{ t('reports.safeAdministrators.colSafe') }}</th>
            <th>{{ t('reports.safeAdministrators.colSource') }}</th>
            <th>{{ t('reports.safeAdministrators.colPerson') }}</th>
            <th>{{ t('reports.safeAdministrators.colName') }}</th>
            <th>{{ t('reports.safeAdministrators.colEnabled') }}</th>
            <th>{{ t('reports.safeAdministrators.colManageSafe') }}</th>
            <th>{{ t('reports.safeAdministrators.colManageSafeMembers') }}</th>
            <th>{{ t('reports.safeAdministrators.colFullRights') }}</th>
            <th>{{ t('reports.safeAdministrators.colHow') }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="r in shown" :key="`${r.safeKey}|${r.personName}`">
            <td><router-link :to="{ name: 'safe-detail', params: { safeKey: r.safeKey } }">{{ r.safeName }}</router-link></td>
            <td>{{ r.sourceSystemName }}</td>
            <td>{{ r.personName }}<span v-if="r.isBuiltIn" class="row-note"> {{ t('reports.safeAdministrators.builtIn') }}</span></td>
            <td>{{ r.displayName ?? '' }}</td>
            <td>{{ yesNo(r.isEnabled) }}</td>
            <td>{{ yesNo(r.manageSafe) }}</td>
            <td>{{ yesNo(r.manageSafeMembers) }}</td>
            <td>{{ yesNo(r.fullRights) }}</td>
            <td>{{ r.accessPaths }}</td>
          </tr>
        </tbody>
      </table>
      <table v-else>
        <thead>
          <tr>
            <th>{{ t('reports.safeAdministrators.colPerson') }}</th>
            <th>{{ t('reports.safeAdministrators.colName') }}</th>
            <th>{{ t('reports.safeAdministrators.colEnabled') }}</th>
            <th>{{ t('reports.safeAdministrators.colSafes') }}</th>
            <th>{{ t('reports.safeAdministrators.colManageSafe') }}</th>
            <th>{{ t('reports.safeAdministrators.colManageSafeMembers') }}</th>
            <th>{{ t('reports.safeAdministrators.colFullRights') }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="p in people" :key="p.personName">
            <td>{{ p.personName }}<span v-if="p.isBuiltIn" class="row-note"> {{ t('reports.safeAdministrators.builtIn') }}</span></td>
            <td>{{ p.displayName ?? '' }}</td>
            <td>{{ yesNo(p.isEnabled) }}</td>
            <td>{{ formatCount(p.safes) }}</td>
            <td>{{ formatCount(p.manageSafeSafes) }}</td>
            <td>{{ formatCount(p.manageSafeMembersSafes) }}</td>
            <td>{{ formatCount(p.fullRightsSafes) }}</td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
}
.row-note {
  font-style: italic;
}
</style>
