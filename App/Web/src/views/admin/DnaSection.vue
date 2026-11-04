<script setup>
// D-325: CyberArk Discovery & Audit (DNA) as a data source on Admin > Data Sources: upload DNA's account-list
// workbook (it replaces the previous one) and see what the latest upload held. Its hard-coded credentials sheet is
// never read. The accounts are on Reports > DNA Accounts.
import { ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatRunTime } from './dataFeedFormat'
import { formatCount } from '../../utils/formatCount'
import { problemMessage } from '../../utils/problemText'

const { t } = useI18n()

const latest = ref(null)
const file = ref(null)
const busy = ref(false)
const error = ref(null)
const notice = ref(null)

async function load() {
  const response = await fetch('/api/admin/dna')
  if (response.ok) latest.value = (await response.json()).latest
}

async function upload() {
  busy.value = true
  error.value = null
  notice.value = null
  try {
    const form = new FormData()
    form.append('file', file.value)
    const response = await fetch('/api/admin/dna/import', { method: 'POST', body: form })
    if (!response.ok) throw new Error(await problemMessage(response, t('common.requestFailed', { status: response.status })))
    latest.value = (await response.json()).latest
    notice.value = t('admin.dna.imported')
  } catch (err) {
    error.value = err.message
  } finally {
    busy.value = false
  }
}

onMounted(load)
</script>

<template>
  <section class="dna-source">
    <h3>{{ t('admin.dna.heading') }}</h3>
    <p>{{ t('admin.dna.intro') }}</p>
    <form class="filter-row" @submit.prevent="upload">
      <label class="field-label"><span class="field-label-text">{{ t('admin.dna.file') }}</span>
        <input type="file" accept=".xlsx" required @change="file = $event.target.files[0] ?? null" /></label>
      <button type="submit" class="btn-primary" :disabled="busy || !file">{{ t('admin.dna.import') }}</button>
    </form>
    <p v-if="notice" role="status">{{ notice }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="latest">
      {{ t('admin.dna.latest', { file: latest.fileName, when: formatRunTime(latest.importedAt), by: latest.importedByName ?? '', count: formatCount(latest.accountCount), tracked: formatCount(latest.trackedCount) }) }}
      <br /><small>{{ latest.sheetCounts }}</small>
      <br /><router-link :to="{ name: 'reports-dna-accounts' }">{{ t('admin.dna.seeAccounts') }}</router-link>
    </p>
    <p v-else>{{ t('admin.dna.none') }}</p>
  </section>
</template>
