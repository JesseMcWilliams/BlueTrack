<script setup>
// D-220: the Export CSV / Export Excel buttons on every report page. The API
// builds the file (GET <path>?format=csv|xlsx plus the page's own filter and
// sort parameters) from every row the report's filters match, not just the
// page shown. fetch('/api/...') goes through installApiFetch (apiBase.js), so
// the request reaches the API's base with the usual header and cookies; the
// file is saved from a blob under the name the API gives (Content-Disposition).
// A refusal (too many rows, no permission) shows the API's own message.
import { ref } from 'vue'
import { fileNameFrom } from '../utils/contentDisposition'

const props = defineProps({
  // The report's export endpoint, e.g. '/api/reports/risk-score/export'.
  path: { type: String, required: true },
  // The page's current filters and sort; empty, null and false values are left out.
  params: { type: Object, default: () => ({}) }
})

const busy = ref(null)
const error = ref(null)
const done = ref(null)

const formats = [
  { format: 'csv', label: 'Export CSV' },
  { format: 'xlsx', label: 'Export Excel' }
]

async function problemMessage(response) {
  const problem = await response.json().catch(() => null)
  return problem?.detail ?? problem?.title ?? `Export failed: ${response.status}`
}

async function download(format) {
  busy.value = format
  error.value = null
  done.value = null
  try {
    const query = new URLSearchParams({ format })
    for (const [key, value] of Object.entries(props.params ?? {})) {
      if (value !== null && value !== undefined && value !== '' && value !== false) query.set(key, String(value))
    }
    const response = await fetch(`${props.path}?${query.toString()}`)
    if (!response.ok) throw new Error(await problemMessage(response))

    const blob = await response.blob()
    const fileName = fileNameFrom(response.headers.get('Content-Disposition')) ?? `BlueTrack_Report.${format}`
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = fileName
    document.body.appendChild(link)
    link.click()
    link.remove()
    // Revoked later, not at once: some browsers still read the blob after click() returns.
    setTimeout(() => URL.revokeObjectURL(url), 40_000)
    done.value = `Downloaded ${fileName}.`
  } catch (err) {
    error.value = err.message
  } finally {
    busy.value = null
  }
}
</script>

<template>
  <div class="report-export" role="group" aria-label="Export this report">
    <button
      v-for="f in formats"
      :key="f.format"
      type="button"
      :disabled="busy !== null"
      :aria-busy="busy === f.format ? 'true' : 'false'"
      @click="download(f.format)"
    >{{ f.label }}</button>
    <span v-if="busy" role="status">Preparing the export...</span>
    <span v-else-if="done" role="status">{{ done }}</span>
    <p v-if="error" class="report-export-error" role="alert">Could not export: {{ error }}</p>
  </div>
</template>

<style scoped>
.report-export {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--space-2);
  margin: var(--space-2) 0 var(--space-3);
}

.report-export-error {
  flex-basis: 100%;
  margin: 0;
  color: var(--color-error-text);
}
</style>
