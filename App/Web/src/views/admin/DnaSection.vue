<script setup>
// D-325: CyberArk Discovery & Audit (DNA) as a data source on Admin > Data Sources: upload DNA's workbook (it replaces
// the previous one) and see what the latest upload held. Its hard-coded credentials sheet goes to its own table and report (D-345). The accounts
// are on Reports > DNA Accounts. D-341: a data feed (type DnaReport) can import it from a folder each night instead.
// D-342: tracking rules. D-343: Purge, and how long DNA data is kept. D-344: the columns only holders of
// ViewDnaSensitiveColumns may see.
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatRunTime } from './dataFeedFormat'
import { formatCount } from '../../utils/formatCount'
import { problemMessage } from '../../utils/problemText'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { byLabel } from '../../utils/sortOptions'
import { dnaColumnLabels } from './dnaLabels'
import DnaTrackRules from './DnaTrackRules.vue'
import { useRightsStore } from '../../stores/rights'

const { t } = useI18n()
const rights = useRightsStore()

const latest = ref(null)
const file = ref(null)
const busy = ref(false)
const error = ref(null)
const notice = ref(null)
const restrictable = ref([])
const retentionDays = ref('')
const restricted = ref(new Set())
const settingsNotice = ref(null)
const settingsError = ref(null)

const columnNames = computed(() => dnaColumnLabels(t))
const sortedColumns = computed(() => byLabel(restrictable.value, c => columnNames.value[c] ?? c))

async function load() {
  const response = await fetch('/api/admin/dna')
  if (!response.ok) return
  const body = await response.json()
  latest.value = body.latest
  restrictable.value = body.restrictableColumns ?? []
  retentionDays.value = body.settings?.retentionDays ?? ''
  restricted.value = new Set(body.settings?.restrictedColumns ?? [])
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
    const body = await response.json()
    latest.value = body.latest
    notice.value = body.trackedByRules
      ? t('admin.dna.importedTracked', { count: formatCount(body.trackedByRules) }, body.trackedByRules)
      : t('admin.dna.imported')
  } catch (err) {
    error.value = err.message
  } finally {
    busy.value = false
  }
}

// D-343: everything goes now; accounts already tracked in BlueTrack stay.
async function purge() {
  if (!(await confirmDelete(t('admin.dna.confirmPurge'), t('admin.dna.purge')))) return
  error.value = null
  notice.value = null
  const response = await fetch('/api/admin/dna/purge', { method: 'POST' })
  if (!response.ok) {
    error.value = await problemMessage(response, t('common.requestFailed', { status: response.status }))
    return
  }
  const { deleted } = await response.json()
  notice.value = t('admin.dna.purged', { count: formatCount(deleted) }, deleted)
  await load()
}

function toggleColumn(column, on) {
  const next = new Set(restricted.value)
  if (on) next.add(column)
  else next.delete(column)
  restricted.value = next
}

async function saveSettings() {
  settingsError.value = null
  settingsNotice.value = null
  const days = String(retentionDays.value).trim()
  const response = await fetch('/api/admin/dna/settings', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ retentionDays: days === '' ? null : Number(days), restrictedColumns: [...restricted.value] })
  })
  if (!response.ok) {
    settingsError.value = await problemMessage(response, t('common.requestFailed', { status: response.status }))
    return
  }
  settingsNotice.value = t('admin.dna.settingsSaved')
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
    <p class="hint">{{ t('admin.dna.feedHint') }}</p>
    <p v-if="notice" role="status">{{ notice }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="latest">
      {{ t('admin.dna.latest', { file: latest.fileName, when: formatRunTime(latest.importedAt), by: latest.importedByName ?? '', count: formatCount(latest.accountCount), tracked: formatCount(latest.trackedCount) }) }}
      <br /><small>{{ latest.sheetCounts }}</small>
      <template v-if="latest.hardCodedCount"><br /><small>{{ t('admin.dna.hardCodedLatest', { count: formatCount(latest.hardCodedCount) }, latest.hardCodedCount) }}</small>
        <template v-if="rights.hasPermission('ViewDnaSensitiveColumns')">{{ ' ' }}<router-link :to="{ name: 'reports-dna-hard-coded-credentials' }">{{ t('admin.dna.seeHardCoded') }}</router-link></template></template>
      <br /><router-link :to="{ name: 'reports-dna-accounts' }">{{ t('admin.dna.seeAccounts') }}</router-link>
    </p>
    <p v-else>{{ t('admin.dna.none') }}</p>
    <p v-if="latest"><button type="button" class="purge" @click="purge">{{ t('admin.dna.purge') }}</button></p>

    <DnaTrackRules />

    <form class="dna-settings" @submit.prevent="saveSettings">
      <h4>{{ t('admin.dna.settingsHeading') }}</h4>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.dna.retention') }}</span>
        <input v-model="retentionDays" type="number" min="1" max="3650" /></label>
        <small>{{ ' ' + t('admin.dna.retentionHint') }}</small></p>
      <fieldset>
        <legend>{{ t('admin.dna.restrictedHeading') }}</legend>
        <p class="hint">{{ t('admin.dna.restrictedHint') }}</p>
        <div class="restricted-columns">
          <label v-for="c in sortedColumns" :key="c">
            <input type="checkbox" :checked="restricted.has(c)" @change="toggleColumn(c, $event.target.checked)" /> {{ columnNames[c] ?? c }}
          </label>
        </div>
      </fieldset>
      <p><button type="submit" class="btn-primary">{{ t('admin.dna.saveSettings') }}</button></p>
      <p v-if="settingsNotice" role="status">{{ settingsNotice }}</p>
      <p v-if="settingsError" role="alert">{{ settingsError }}</p>
    </form>
  </section>
</template>

<style scoped>
.restricted-columns {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(14rem, 1fr));
  gap: 0.25rem 1rem;
}
</style>
