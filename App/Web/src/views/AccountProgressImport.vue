<script setup>
// D-318: reads an Accounts export (CSV) back with changed values. "Account Key" finds each account; each editable
// column's non-blank cells are applied with the edit page's rules. The result says how many accounts changed and
// which rows couldn't be applied, and why.
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatCount } from '../utils/formatCount'
import { problemMessage } from '../utils/problemText'

const emit = defineEmits(['imported'])
const { t } = useI18n()

const open = ref(false)
const file = ref(null)
const reason = ref('')
// D-323: off by default, so a blank cell never clears anything unless asked.
const clearBlanks = ref(false)
const busy = ref(false)
const error = ref(null)
const result = ref(null)

async function importFile() {
  busy.value = true
  error.value = null
  result.value = null
  try {
    const form = new FormData()
    form.append('file', file.value)
    if (reason.value.trim()) form.append('reason', reason.value.trim())
    if (clearBlanks.value) form.append('clearBlanks', 'true')
    const response = await fetch('/api/account-progress/import', { method: 'POST', body: form })
    if (!response.ok) throw new Error(await problemMessage(response, t('common.requestFailed', { status: response.status })))
    result.value = await response.json()
    emit('imported')
  } catch (err) {
    error.value = err.message
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="account-import">
    <button type="button" :aria-expanded="open" @click="open = !open">{{ t('pages.accountProgressImport.toggle') }}</button>
    <form v-if="open" class="account-import-form" @submit.prevent="importFile">
      <p>{{ t('pages.accountProgressImport.intro') }}</p>
      <label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressImport.file') }}</span>
        <input type="file" accept=".csv,text/csv,.xlsx" required @change="file = $event.target.files[0] ?? null" /></label>
      <label class="field-label"><span class="field-label-text">{{ t('pages.accountProgressImport.reason') }}</span>
        <input v-model="reason" size="50" maxlength="1000" /></label>
      <label class="account-import-clear"><input v-model="clearBlanks" type="checkbox" /> {{ t('pages.accountProgressImport.clearBlanks') }}</label>
      <button type="submit" class="btn-primary" :disabled="busy || !file">{{ t('pages.accountProgressImport.import') }}</button>
    </form>
    <p v-if="error" role="alert">{{ error }}</p>
    <div v-if="result" role="status">
      <p>{{ t('pages.accountProgressImport.done', { rows: formatCount(result.rows), updated: formatCount(result.updated), unchanged: formatCount(result.unchanged), problems: formatCount(result.problems.length) }) }}</p>
      <ul v-if="result.problems.length">
        <li v-for="p in result.problems" :key="p.rowNumber">{{ t('pages.accountProgressImport.problem', { row: p.rowNumber, reason: p.reason }) }}</li>
      </ul>
    </div>
  </div>
</template>

<style scoped>
.account-import { margin: var(--space-2) 0; }
.account-import-clear { display: block; margin: var(--space-2) 0; }
.account-import-form {
  border: 1px solid var(--color-border);
  padding: var(--space-2) var(--space-3);
  margin-top: var(--space-2);
}
</style>
