<script setup>
// Single-row settings form against /api/admin/configuration
// (GlobalApplicationConfigController) -- merges web.app_config and
// web.audit_config. LogReadEvents/RetentionDays are stored and editable
// here, but nothing in the API actually enforces read-logging yet
// (AuditLogger's own comment on why) -- this page manages the setting,
// not the enforcement.
import { ref, computed, onMounted } from 'vue'

// D-181: data feed schedule settings. BusinessDays is stored as
// "Mon,Tue,..."; the page edits it as one checkbox per day.
const dayNames = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']

// D-186: decommission / ignored-safe name patterns, with one helper that
// shows which sample names each (unsaved) pattern matches, evaluated by
// the API with the same SQL function the reports and the import use.
const patterns = [
  { key: 'safeDecom', label: 'Safe decommission pattern', hint: 'Safes being retired (e.g. a DEL_ prefix or _DECOM suffix): listed on the "flagged for deletion" reports; their accounts are marked.' },
  { key: 'accountDecom', label: 'Account decommission pattern', hint: 'Accounts whose own name marks them for deletion.' },
  { key: 'safeIgnore', label: 'Ignored safe pattern', hint: 'Safes left out of the nightly import (e.g. a ZZ_ prefix); accounts already imported from them are flagged deleted.' }
]
const sampleNames = ref('DEL_Finance\nFinance_DECOM\nZZ_Archive\nFinance')
const patternTest = ref(null)
const patternTesting = ref(false)

function patternOf(key) {
  return { mode: config.value[`${key}Mode`], value: config.value[`${key}Value`] }
}

async function testPatterns() {
  patternTesting.value = true
  try {
    const response = await fetch('/api/admin/configuration/test-patterns', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        names: sampleNames.value.split('\n'),
        safeDecom: patternOf('safeDecom'),
        accountDecom: patternOf('accountDecom'),
        safeIgnore: patternOf('safeIgnore')
      })
    })
    patternTest.value = response.ok ? await response.json() : { error: `Test failed: ${response.status}` }
  } finally {
    patternTesting.value = false
  }
}

const config = ref(null)
const error = ref(null)
const loading = ref(true)
const saved = ref(false)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/configuration')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    config.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

const businessDays = computed({
  get: () => (config.value?.businessDays ?? '').split(',').map(d => d.trim()).filter(Boolean),
  set: days => { config.value.businessDays = dayNames.filter(d => days.includes(d)).join(',') }
})

async function save() {
  saved.value = false
  error.value = null
  const response = await fetch('/api/admin/configuration', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(config.value)
  })
  if (!response.ok) {
    const problem = response.status === 400 ? await response.json().catch(() => null) : null
    error.value = problem?.detail ? `Save failed: ${problem.detail}` : `Save failed: ${response.status}`
    return
  }
  saved.value = true
}
</script>

<template>
  <div>
    <h2>Global Application Configuration</h2>
    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="error && !config" role="alert">{{ error }}</p>
    <form v-else @submit.prevent="save">
      <p>
        <label class="field-label"><span class="field-label-text">Idle Timeout (minutes):</span> <input v-model.number="config.idleTimeoutMinutes" type="number" required /></label>
      </p>
      <p>
        <label class="field-label">
          <span class="field-label-text">Breadcrumb Position:</span>
          <select v-model="config.breadcrumbPosition">
            <option value="TopLeft">Top Left</option>
            <option value="TopRight">Top Right</option>
          </select>
        </label>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">Exception ID Pattern:</span> <input v-model="config.exceptionIdPattern" required /></label>
        <small>Tokens: {yyyy}, {yy}, {seq:0000} (padding width from the number of zeros)</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">Account Progress Lock Timeout (minutes):</span> <input v-model.number="config.lockTimeoutMinutes" type="number" required /></label>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">Audit Retention (days, blank = keep forever):</span> <input v-model.number="config.retentionDays" type="number" /></label>
      </p>
      <p>
        <label><input v-model="config.logReadEvents" type="checkbox" /> Log read/view events (off by default, D-35)</label>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">Backup Folder:</span> <input v-model="config.backupFolder" placeholder="D:\Backups\BlueTrack" /></label>
        <small>Where the Deployment page's "Backup App" button writes the database backup file.</small>
      </p>
      <p>
        <label class="field-label">
          <span class="field-label-text">Risk Score Algorithm:</span>
          <select v-model="config.activeRiskAlgorithm">
            <option value="DominantPlusTail">Dominant Plus Tail</option>
            <option value="CombinedExposure">Combined Exposure</option>
          </select>
        </label>
        <small>Which of the two candidate algorithms usp_CalculateRiskScore uses (D-119 Phase D).</small>
      </p>
      <p>
        <label><input v-model="config.enforceRiskExceptionSegregationOfDuties" type="checkbox" /> Enforce segregation of duties on Risk Exception approval</label>
        <small>When enabled, the person who approved a Risk Exception cannot also be the one who links it to an account. Leave off if your organization doesn't have separate staff for the two roles.</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">Bulk edit limit (accounts):</span> <input v-model.number="config.bulkEditMaxAccounts" type="number" min="1" max="10000" required /></label>
        <small>The most accounts one Account Progress bulk edit may change (D-182).</small>
      </p>
      <fieldset class="patterns">
        <legend>Decommissioned and ignored safes</legend>
        <div class="pattern-helper">
          <p>
            <strong>Try your patterns</strong> on sample names (one per line). <em>Prefix</em> matches the start of the name,
            <em>Suffix</em> the end; both ignore case.
            <template v-if="config.regexSupported">
              <em>Regex</em> is a regular expression, also ignoring case. Example: <code>^(DEL|ZZ)_|_DECOM$</code> matches
              <code>DEL_Finance</code>, <code>zz_Archive</code> and <code>Finance_Decom</code>, but not <code>Finance</code> or <code>MODEL_X</code>.
            </template>
            <template v-else>
              <em>Regex</em> needs SQL Server 2025, so it isn't available on this server.
            </template>
          </p>
          <textarea v-model="sampleNames" rows="4" cols="40" aria-label="Sample names"></textarea>
          <p><button type="button" :disabled="patternTesting" @click="testPatterns">Test patterns</button></p>
          <p v-if="patternTest?.error" role="alert">{{ patternTest.error }}</p>
          <template v-else-if="patternTest">
            <p v-for="(message, key) in patternTest.errors" :key="key" role="alert">{{ patterns.find(x => x.key === key.charAt(0).toLowerCase() + key.slice(1))?.label }}: {{ message }}</p>
            <table class="pattern-results">
              <thead><tr><th>Name</th><th v-for="x in patterns" :key="x.key">{{ x.label }}</th></tr></thead>
              <tbody>
                <tr v-for="row in patternTest.rows" :key="row.name">
                  <td>{{ row.name }}</td>
                  <td v-for="x in patterns" :key="x.key">{{ row[x.key] ? 'Matches' : '—' }}</td>
                </tr>
              </tbody>
            </table>
          </template>
        </div>
        <p v-for="x in patterns" :key="x.key">
          <label class="field-label">
            <span class="field-label-text">{{ x.label }}:</span>
            <select v-model="config[`${x.key}Mode`]" :aria-label="`${x.label} type`">
              <option value="Off">Off</option>
              <option value="Prefix">Prefix</option>
              <option value="Suffix">Suffix</option>
              <option value="Regex" :disabled="!config.regexSupported">Regex{{ config.regexSupported ? '' : ' (needs SQL Server 2025)' }}</option>
            </select>
          </label>
          <input v-model="config[`${x.key}Value`]" :disabled="config[`${x.key}Mode`] === 'Off'" maxlength="200" :aria-label="`${x.label} value`" />
          <br /><small>{{ x.hint }}</small>
        </p>
      </fieldset>
      <fieldset>
        <legend>Data feeds (Admin → Data Sources)</legend>
        <p>
          <label class="field-label"><span class="field-label-text">Nightly run time:</span> <input v-model="config.dataFeedRunTime" type="time" required /></label>
          <small>Server local time. Default 04:00, after the 02:00 Import and Load job.</small>
        </p>
        <p>
          <label class="field-label"><span class="field-label-text">Keep run history (days):</span> <input v-model.number="config.dataFeedRunRetentionDays" type="number" min="1" max="365" required /></label>
        </p>
        <p>
          <label class="field-label"><span class="field-label-text">Business hours start:</span> <input v-model="config.businessHoursStart" type="time" required /></label>
          <label class="field-label"><span class="field-label-text">Business hours end:</span> <input v-model="config.businessHoursEnd" type="time" required /></label>
        </p>
        <p>
          <span class="field-label-text">Business days:</span>
          <label v-for="day in dayNames" :key="day"><input v-model="businessDays" type="checkbox" :value="day" /> {{ day }}</label>
          <br /><small>Run now asks for confirmation inside these hours, since an import changes data people are working with.</small>
        </p>
      </fieldset>
      <p v-if="error" role="alert">{{ error }}</p>
      <button type="submit" class="btn-primary">Save</button>
      <span v-if="saved" role="status"> Saved.</span>
    </form>
  </div>
</template>
