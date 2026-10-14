<script setup>
import { apiUrl } from '../utils/apiBase'
// D-124 Phase 5: relocated wholesale off Targets.vue's own always-visible
// inline sections onto this dedicated routed page (targets-bulk-import),
// reached via the "Bulk Actions" link in Targets.vue's header -- consistent
// with how Add/Edit moved to TargetEdit.vue in Phase 4. The file-input +
// Import button + result-summary markup and the backing
// importFile/onInventoryFileChange/onAccountTargetFileChange script logic
// are unchanged from Targets.vue; the one deliberate change is dropping the
// post-import `await load()` call that used to refresh Targets.vue's own
// table -- there's no table on this page, and navigating back to Targets
// re-fetches fresh data on mount anyway.
//
// Targets.vue's separate "Link a Single Account to a Target" section
// (a single-record form, not a CSV/file-upload import) is NOT a bulk-import
// section and stays on Targets.vue untouched.
import { ref } from 'vue'
import { formatCount } from '../utils/formatCount'
import MappingProfileSelect from '../components/MappingProfileSelect.vue'
import CsvColumnCheck from '../components/CsvColumnCheck.vue' // D-225
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const inventoryFile = ref(null)
const inventoryProfile = ref(null) // D-207
const accountTargetProfile = ref(null)
const inventoryImportResult = ref(null)
const inventoryImporting = ref(false)

const accountTargetFile = ref(null)
const accountTargetImportResult = ref(null)
const accountTargetImporting = ref(false)

async function importFile(url, fileRef, resultRef, importingRef, profileRef) {
  if (!fileRef.value) return
  importingRef.value = true
  resultRef.value = null
  try {
    const formData = new FormData()
    formData.append('file', fileRef.value)
    if (profileRef.value !== null) formData.append('mappingProfileKey', String(profileRef.value)) // D-207
    const response = await fetch(url, { method: 'POST', body: formData })
    resultRef.value = response.ok ? await response.json() : { error: t('common.importFailed', { status: response.status }) }
  } finally {
    importingRef.value = false
  }
}

function onInventoryFileChange(event) {
  inventoryFile.value = event.target.files[0] ?? null
}
function onAccountTargetFileChange(event) {
  accountTargetFile.value = event.target.files[0] ?? null
}

async function importInventory() {
  await importFile('/api/admin/risk-scoring/import/target-inventory', inventoryFile, inventoryImportResult, inventoryImporting, inventoryProfile)
}
async function importAccountTargetMap() {
  await importFile('/api/admin/risk-scoring/import/account-target-map', accountTargetFile, accountTargetImportResult, accountTargetImporting, accountTargetProfile)
}
</script>

<template>
  <div>
    <h1>{{ t('pages.targetsBulkImport.title') }}</h1>

    <h3>{{ t('pages.targetsBulkImport.inventoryHeading') }}</h3>
    <p>
      <a :href="apiUrl('/api/admin/risk-scoring/import/target-inventory/template')">{{ t('common.downloadTemplate') }}</a> {{ t('pages.targetsBulkImport.inventoryIntro') }}
    </p>
    <p class="filter-row">
      <input type="file" accept=".csv" :aria-label="t('pages.targetsBulkImport.inventoryFileLabel')" @change="onInventoryFileChange" />
      <MappingProfileSelect v-model="inventoryProfile" feed-type="TargetInventory" area="target" />
      <button :disabled="!inventoryFile || inventoryImporting" @click="importInventory">{{ inventoryImporting ? t('common.importing') : t('pages.targetsBulkImport.import') }}</button>
    </p>
    <CsvColumnCheck :file="inventoryFile" feed-type="TargetInventory" :profile-chosen="inventoryProfile !== null" />
    <p v-if="inventoryImportResult">
      {{ t('pages.targetsBulkImport.inventorySummary', { total: formatCount(inventoryImportResult.totalRows), created: formatCount(inventoryImportResult.createdCount), merged: formatCount(inventoryImportResult.mergedCount), pending: formatCount(inventoryImportResult.pendingReviewCount), errors: formatCount(inventoryImportResult.errors?.length ?? 0) }) }}
      <span v-if="inventoryImportResult.errors?.length"><br />{{ inventoryImportResult.errors.map(e => t('pages.targetsBulkImport.rowError', { row: e.rowNumber, error: e.error })).join('; ') }}</span>
    </p>

    <h3>{{ t('pages.targetsBulkImport.linksHeading') }}</h3>
    <p><a :href="apiUrl('/api/admin/risk-scoring/import/account-target-map/template')">{{ t('common.downloadTemplate') }}</a> {{ t('pages.targetsBulkImport.linksIntro') }}</p>
    <p class="filter-row">
      <input type="file" accept=".csv" :aria-label="t('pages.targetsBulkImport.linksFileLabel')" @change="onAccountTargetFileChange" />
      <MappingProfileSelect v-model="accountTargetProfile" feed-type="AccountTargetMap" area="target" />
      <button :disabled="!accountTargetFile || accountTargetImporting" @click="importAccountTargetMap">{{ accountTargetImporting ? t('common.importing') : t('pages.targetsBulkImport.import') }}</button>
    </p>
    <CsvColumnCheck :file="accountTargetFile" feed-type="AccountTargetMap" :profile-chosen="accountTargetProfile !== null" />
    <p v-if="accountTargetImportResult">
      {{ t('pages.targetsBulkImport.linksSummary', { total: formatCount(accountTargetImportResult.totalRows), created: formatCount(accountTargetImportResult.createdCount), merged: formatCount(accountTargetImportResult.mergedCount), errors: formatCount(accountTargetImportResult.errors?.length ?? 0) }) }}
      <span v-if="accountTargetImportResult.errors?.length"><br />{{ accountTargetImportResult.errors.map(e => t('pages.targetsBulkImport.rowError', { row: e.rowNumber, error: e.error })).join('; ') }}</span>
    </p>
  </div>
</template>
