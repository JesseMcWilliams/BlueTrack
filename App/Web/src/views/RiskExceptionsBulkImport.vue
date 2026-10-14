<script setup>
import { apiUrl } from '../utils/apiBase'
// D-183: import risk exceptions approved in another tool, reached via the
// Risk Exceptions list's "Bulk Actions" button -- the same upload / Import
// / result-summary pattern as ApplicationSafeMappingBulkImport.vue. Each
// row gets a BlueTrack ExceptionID and keeps its source tool, ID and link.
import { ref } from 'vue'
import { formatCount } from '../utils/formatCount'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const file = ref(null)
const result = ref(null)
const importing = ref(false)

async function importFile() {
  if (!file.value) return
  importing.value = true
  result.value = null
  try {
    const formData = new FormData()
    formData.append('file', file.value)
    const response = await fetch('/api/risk-exceptions/import', { method: 'POST', body: formData })
    result.value = response.ok ? await response.json() : { error: t('common.importFailed', { status: response.status }) }
  } finally {
    importing.value = false
  }
}
</script>

<template>
  <div>
    <h1>{{ t('pages.riskExceptionsBulkImport.title') }}</h1>

    <h3>{{ t('pages.riskExceptionsBulkImport.importHeading') }}</h3>
    <p><a :href="apiUrl('/api/risk-exceptions/import/template')">{{ t('common.downloadTemplate') }}</a> {{ t('pages.riskExceptionsBulkImport.templateIntro') }}</p>
    <ul>
      <i18n-t keypath="pages.riskExceptionsBulkImport.ruleSource" tag="li" scope="global"><template #sourceTool><strong>{{ 'SourceTool' }}</strong></template><template #sourceExceptionId><strong>{{ 'SourceExceptionId' }}</strong></template></i18n-t>
      <i18n-t keypath="pages.riskExceptionsBulkImport.ruleSourceUrl" tag="li" scope="global"><template #sourceUrl><strong>{{ 'SourceUrl' }}</strong></template></i18n-t>
      <i18n-t keypath="pages.riskExceptionsBulkImport.ruleScope" tag="li" scope="global"><template #accountUserName><strong>{{ 'AccountUserName' }}</strong></template><template #accountAddress><strong>{{ 'AccountAddress' }}</strong></template><template #applicationCode><strong>{{ 'ApplicationCode' }}</strong></template></i18n-t>
      <i18n-t keypath="pages.riskExceptionsBulkImport.ruleApproval" tag="li" scope="global"><template #justification><strong>{{ 'Justification' }}</strong></template><template #approvedByName><strong>{{ 'ApprovedByName' }}</strong></template><template #approvalDate><strong>{{ 'ApprovalDate' }}</strong></template><template #reviewDate><strong>{{ 'ReviewDate' }}</strong></template></i18n-t>
      <i18n-t keypath="pages.riskExceptionsBulkImport.ruleStatus" tag="li" scope="global"><template #status><strong>{{ 'Status' }}</strong></template><template #externalTicketReference><strong>{{ 'ExternalTicketReference' }}</strong></template></i18n-t>
      <i18n-t keypath="pages.riskExceptionsBulkImport.ruleLink" tag="li" scope="global"><template #linkToAccountProgress><strong>{{ 'LinkToAccountProgress' }}</strong></template></i18n-t>
    </ul>
    <p class="filter-row">
      <input type="file" accept=".csv" :aria-label="t('pages.riskExceptionsBulkImport.fileLabel')" @change="file = $event.target.files[0] ?? null" />
      <button :disabled="!file || importing" @click="importFile">{{ importing ? t('common.importing') : t('pages.riskExceptionsBulkImport.import') }}</button>
    </p>
    <div v-if="result" data-testid="exceptions-result" role="status">
      <p v-if="result.error">{{ result.error }}</p>
      <template v-else>
        <p>{{ t('pages.riskExceptionsBulkImport.summary', { total: formatCount(result.totalRows), created: formatCount(result.createdCount), linked: formatCount(result.linkedCount), errors: formatCount(result.errors.length) }) }}</p>
        <ul v-if="result.errors.length">
          <li v-for="e in result.errors" :key="`${e.rowNumber}-${e.error}`">{{ t('pages.riskExceptionsBulkImport.rowError', { row: e.rowNumber, error: e.error }) }}</li>
        </ul>
        <p v-if="result.created.length"><small>{{ t('pages.riskExceptionsBulkImport.newIds', { ids: result.created.map(c => `${c.sourceExceptionId} → ${c.exceptionId}`).join(', ') }) }}</small></p>
      </template>
    </div>
  </div>
</template>
