<script setup>
import { apiUrl } from '../utils/apiBase'
// D-124 Phase 5: relocated wholesale off AccessGroups.vue's own
// always-visible inline sections onto this dedicated routed page
// (access-groups-bulk-import), reached via the "Bulk Actions" link in
// AccessGroups.vue's header -- consistent with how Add/Edit moved to
// AccessGroupEdit.vue in Phase 4. The importState/onFileChange/importFile
// script logic and the file-input + Import button + result-summary markup
// for all 3 sections are unchanged from AccessGroups.vue; the one
// deliberate change is dropping the post-import `await load()` call that
// used to refresh AccessGroups.vue's own table -- there's no table on this
// page, and navigating back to Access Groups re-fetches fresh data on
// mount anyway.
import { ref } from 'vue'
import { formatCount } from '../utils/formatCount'
import MappingProfileSelect from '../components/MappingProfileSelect.vue'
import CsvColumnCheck from '../components/CsvColumnCheck.vue' // D-225
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const importState = ref({
  inventory: { file: null, result: null, importing: false, profile: null },
  targetMap: { file: null, result: null, importing: false, profile: null },
  membership: { file: null, result: null, importing: false, profile: null }
})

function onFileChange(key, event) {
  importState.value[key].file = event.target.files[0] ?? null
}

async function importFile(key, url) {
  const state = importState.value[key]
  if (!state.file) return
  state.importing = true
  state.result = null
  try {
    const formData = new FormData()
    formData.append('file', state.file)
    if (state.profile !== null) formData.append('mappingProfileKey', String(state.profile)) // D-207
    const response = await fetch(url, { method: 'POST', body: formData })
    state.result = response.ok ? await response.json() : { error: t('common.importFailed', { status: response.status }) }
  } finally {
    state.importing = false
  }
}
</script>

<template>
  <div>
    <h1>{{ t('pages.accessGroupsBulkImport.title') }}</h1>

    <h3>{{ t('pages.accessGroupsBulkImport.inventoryHeading') }}</h3>
    <p><a :href="apiUrl('/api/admin/risk-scoring/import/access-group-inventory/template')">{{ t('common.downloadTemplate') }}</a> {{ t('pages.accessGroupsBulkImport.inventoryIntro') }}</p>
    <p class="filter-row">
      <input type="file" accept=".csv" :aria-label="t('pages.accessGroupsBulkImport.inventoryFileLabel')" @change="onFileChange('inventory', $event)" />
      <MappingProfileSelect v-model="importState.inventory.profile" feed-type="AccessGroupInventory" area="access-group" />
      <button :disabled="!importState.inventory.file || importState.inventory.importing" @click="importFile('inventory', '/api/admin/risk-scoring/import/access-group-inventory')">{{ importState.inventory.importing ? t('common.importing') : t('pages.accessGroupsBulkImport.import') }}</button>
    </p>
    <CsvColumnCheck :file="importState.inventory.file" feed-type="AccessGroupInventory" :profile-chosen="importState.inventory.profile !== null" />
    <p v-if="importState.inventory.result">
      {{ t('pages.accessGroupsBulkImport.summary', { total: formatCount(importState.inventory.result.totalRows), succeeded: formatCount(importState.inventory.result.succeededCount), errors: formatCount(importState.inventory.result.errors?.length ?? 0) }) }}
      <span v-if="importState.inventory.result.errors?.length"><br />{{ importState.inventory.result.errors.map(e => t('pages.accessGroupsBulkImport.rowError', { row: e.rowNumber, error: e.error })).join('; ') }}</span>
    </p>

    <h3>{{ t('pages.accessGroupsBulkImport.targetMapHeading') }}</h3>
    <p><a :href="apiUrl('/api/admin/risk-scoring/import/access-group-target-map/template')">{{ t('common.downloadTemplate') }}</a> {{ t('pages.accessGroupsBulkImport.targetMapIntro') }}</p>
    <p class="filter-row">
      <input type="file" accept=".csv" :aria-label="t('pages.accessGroupsBulkImport.targetMapFileLabel')" @change="onFileChange('targetMap', $event)" />
      <MappingProfileSelect v-model="importState.targetMap.profile" feed-type="AccessGroupTargetMap" area="access-group" />
      <button :disabled="!importState.targetMap.file || importState.targetMap.importing" @click="importFile('targetMap', '/api/admin/risk-scoring/import/access-group-target-map')">{{ importState.targetMap.importing ? t('common.importing') : t('pages.accessGroupsBulkImport.import') }}</button>
    </p>
    <CsvColumnCheck :file="importState.targetMap.file" feed-type="AccessGroupTargetMap" :profile-chosen="importState.targetMap.profile !== null" />
    <p v-if="importState.targetMap.result">
      {{ t('pages.accessGroupsBulkImport.summary', { total: formatCount(importState.targetMap.result.totalRows), succeeded: formatCount(importState.targetMap.result.succeededCount), errors: formatCount(importState.targetMap.result.errors?.length ?? 0) }) }}
      <span v-if="importState.targetMap.result.errors?.length"><br />{{ importState.targetMap.result.errors.map(e => t('pages.accessGroupsBulkImport.rowError', { row: e.rowNumber, error: e.error })).join('; ') }}</span>
    </p>

    <h3>{{ t('pages.accessGroupsBulkImport.membershipHeading') }}</h3>
    <p><a :href="apiUrl('/api/admin/risk-scoring/import/account-access-group-membership/template')">{{ t('common.downloadTemplate') }}</a> {{ t('pages.accessGroupsBulkImport.membershipIntro') }}</p>
    <p class="filter-row">
      <input type="file" accept=".csv" :aria-label="t('pages.accessGroupsBulkImport.membershipFileLabel')" @change="onFileChange('membership', $event)" />
      <MappingProfileSelect v-model="importState.membership.profile" feed-type="AccountAccessGroupMembership" area="access-group" />
      <button :disabled="!importState.membership.file || importState.membership.importing" @click="importFile('membership', '/api/admin/risk-scoring/import/account-access-group-membership')">{{ importState.membership.importing ? t('common.importing') : t('pages.accessGroupsBulkImport.import') }}</button>
    </p>
    <CsvColumnCheck :file="importState.membership.file" feed-type="AccountAccessGroupMembership" :profile-chosen="importState.membership.profile !== null" />
    <p v-if="importState.membership.result">
      {{ t('pages.accessGroupsBulkImport.summary', { total: formatCount(importState.membership.result.totalRows), succeeded: formatCount(importState.membership.result.succeededCount), errors: formatCount(importState.membership.result.errors?.length ?? 0) }) }}
      <span v-if="importState.membership.result.errors?.length"><br />{{ importState.membership.result.errors.map(e => t('pages.accessGroupsBulkImport.rowError', { row: e.rowNumber, error: e.error })).join('; ') }}</span>
    </p>
  </div>
</template>
