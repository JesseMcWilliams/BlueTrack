<script setup>
// Single-row settings form against /api/admin/configuration
// (GlobalApplicationConfigController) -- merges web.app_config and
// web.audit_config. LogReadEvents/RetentionDays are stored and editable
// here, but nothing in the API actually enforces read-logging yet
// (AuditLogger's own comment on why) -- this page manages the setting,
// not the enforcement.
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

// D-181: data feed schedule settings. BusinessDays is stored as
// "Mon,Tue,..."; the page edits it as one checkbox per day.
const dayNames = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
// The checkbox labels; the stored values above stay English.
const DAY_KEYS = {
  Mon: 'admin.globalApplicationConfiguration.dayMon',
  Tue: 'admin.globalApplicationConfiguration.dayTue',
  Wed: 'admin.globalApplicationConfiguration.dayWed',
  Thu: 'admin.globalApplicationConfiguration.dayThu',
  Fri: 'admin.globalApplicationConfiguration.dayFri',
  Sat: 'admin.globalApplicationConfiguration.daySat',
  Sun: 'admin.globalApplicationConfiguration.daySun'
}

// D-186: decommission / ignored-safe name patterns, with one helper that
// shows which sample names each (unsaved) pattern matches, evaluated by
// the API with the same SQL function the reports and the import use.
const patterns = [
  { key: 'safeDecom', labelKey: 'admin.globalApplicationConfiguration.safeDecomLabel', hintKey: 'admin.globalApplicationConfiguration.safeDecomHint' },
  { key: 'accountDecom', labelKey: 'admin.globalApplicationConfiguration.accountDecomLabel', hintKey: 'admin.globalApplicationConfiguration.accountDecomHint' },
  { key: 'safeIgnore', labelKey: 'admin.globalApplicationConfiguration.safeIgnoreLabel', hintKey: 'admin.globalApplicationConfiguration.safeIgnoreHint' }
]
// The regex example in the pattern helper's text -- sample data, not translated.
const REGEX_EXAMPLE = '^(DEL|ZZ)_|_DECOM$'
const sampleNames = ref('DEL_Finance\nFinance_DECOM\nZZ_Archive\nFinance')
const patternTest = ref(null)
const patternTesting = ref(false)

// The API's error keys are the pattern keys in PascalCase.
function patternLabel(errorKey) {
  const pattern = patterns.find(x => x.key === errorKey.charAt(0).toLowerCase() + errorKey.slice(1))
  return pattern ? t(pattern.labelKey) : ''
}

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
    patternTest.value = response.ok ? await response.json() : { error: t('admin.globalApplicationConfiguration.testFailed', { status: response.status }) }
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
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
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
    // D-200: a blank backup time means "off" (''); null would mean "unchanged".
    // D-224: likewise a blank Site URL means "not set".
    body: JSON.stringify({
      ...config.value,
      scheduledBackupTime: config.value.scheduledBackupTime || '',
      siteUrl: (config.value.siteUrl ?? '').trim()
    })
  })
  if (!response.ok) {
    const problem = response.status === 400 ? await response.json().catch(() => null) : null
    error.value = problem?.detail ? t('admin.globalApplicationConfiguration.saveFailedDetail', { detail: problem.detail }) : t('common.saveFailed', { status: response.status })
    return
  }
  saved.value = true
  // D-224: the API stores the Site URL normalized (no trailing /, lower-case
  // host); show what it kept, without reloading the rest of the form.
  const refreshed = await fetch('/api/admin/configuration').catch(() => null)
  if (refreshed?.ok) config.value.siteUrl = (await refreshed.json()).siteUrl
}
</script>

<template>
  <div>
    <h2>{{ t('admin.globalApplicationConfiguration.title') }}</h2>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error && !config" role="alert">{{ error }}</p>
    <form v-else @submit.prevent="save">
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.globalApplicationConfiguration.siteUrlLabel') }}</span> <input v-model="config.siteUrl" type="url" maxlength="500" :placeholder="t('admin.globalApplicationConfiguration.siteUrlPlaceholder')" /></label>
        <small>{{ t('admin.globalApplicationConfiguration.siteUrlHint') }}</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.globalApplicationConfiguration.idleTimeoutLabel') }}</span> <input v-model.number="config.idleTimeoutMinutes" type="number" min="5" max="720" required /></label>
      </p>
      <p>
        <label class="field-label">
          <span class="field-label-text">{{ t('admin.globalApplicationConfiguration.breadcrumbPositionLabel') }}</span>
          <select v-model="config.breadcrumbPosition">
            <option value="TopLeft">{{ t('admin.globalApplicationConfiguration.topLeft') }}</option>
            <option value="TopRight">{{ t('admin.globalApplicationConfiguration.topRight') }}</option>
          </select>
        </label>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.globalApplicationConfiguration.exceptionIdPatternLabel') }}</span> <input v-model="config.exceptionIdPattern" required /></label>
        <small>{{ t('admin.globalApplicationConfiguration.exceptionIdPatternHint') }}</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.globalApplicationConfiguration.lockTimeoutLabel') }}</span> <input v-model.number="config.lockTimeoutMinutes" type="number" min="1" max="720" required /></label>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.globalApplicationConfiguration.retentionDaysLabel') }}</span> <input v-model.number="config.retentionDays" type="number" /></label>
      </p>
      <p>
        <label><input v-model="config.logReadEvents" type="checkbox" /> {{ t('admin.globalApplicationConfiguration.logReadEvents') }}</label>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.globalApplicationConfiguration.backupFolderLabel') }}</span> <input v-model="config.backupFolder" :placeholder="t('admin.globalApplicationConfiguration.backupFolderPlaceholder')" /></label>
        <small>{{ t('admin.globalApplicationConfiguration.backupFolderHint') }}</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.globalApplicationConfiguration.scheduledBackupTimeLabel') }}</span> <input v-model="config.scheduledBackupTime" type="time" /></label>
        <small>{{ t('admin.globalApplicationConfiguration.scheduledBackupTimeHint') }}</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.globalApplicationConfiguration.scheduledBackupRetentionLabel') }}</span> <input v-model.number="config.scheduledBackupRetentionDays" type="number" min="1" max="3650" required /></label>
        <small>{{ t('admin.globalApplicationConfiguration.scheduledBackupRetentionHint') }}</small>
      </p>
      <p>
        <label class="field-label">
          <span class="field-label-text">{{ t('admin.globalApplicationConfiguration.riskAlgorithmLabel') }}</span>
          <select v-model="config.activeRiskAlgorithm">
            <option value="DominantPlusTail">{{ t('admin.globalApplicationConfiguration.dominantPlusTail') }}</option>
            <option value="CombinedExposure">{{ t('admin.globalApplicationConfiguration.combinedExposure') }}</option>
          </select>
        </label>
        <small>{{ t('admin.globalApplicationConfiguration.riskAlgorithmHint') }}</small>
      </p>
      <p>
        <label><input v-model="config.enforceRiskExceptionSegregationOfDuties" type="checkbox" /> {{ t('admin.globalApplicationConfiguration.enforceSod') }}</label>
        <small>{{ t('admin.globalApplicationConfiguration.enforceSodHint') }}</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.globalApplicationConfiguration.bulkEditLimitLabel') }}</span> <input v-model.number="config.bulkEditMaxAccounts" type="number" min="1" max="10000" required /></label>
        <small>{{ t('admin.globalApplicationConfiguration.bulkEditLimitHint') }}</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.globalApplicationConfiguration.exportMaxRowsLabel') }}</span> <input v-model.number="config.exportMaxRows" type="number" min="1" max="1000000" required /></label>
        <small>{{ t('admin.globalApplicationConfiguration.exportMaxRowsHint') }}</small>
      </p>
      <fieldset class="patterns">
        <legend>{{ t('admin.globalApplicationConfiguration.patternsLegend') }}</legend>
        <div class="pattern-helper">
          <i18n-t v-if="config.regexSupported" keypath="admin.globalApplicationConfiguration.patternHelpRegex" tag="p" scope="global">
            <template #try><strong>{{ t('admin.globalApplicationConfiguration.tryYourPatterns') }}</strong></template>
            <template #prefix><em>{{ t('admin.globalApplicationConfiguration.modePrefix') }}</em></template>
            <template #suffix><em>{{ t('admin.globalApplicationConfiguration.modeSuffix') }}</em></template>
            <template #regex><em>{{ t('admin.globalApplicationConfiguration.modeRegex') }}</em></template>
            <template #example><code>{{ REGEX_EXAMPLE }}</code></template>
            <template #match1><code>{{ 'DEL_Finance' }}</code></template>
            <template #match2><code>{{ 'zz_Archive' }}</code></template>
            <template #match3><code>{{ 'Finance_Decom' }}</code></template>
            <template #nonMatch1><code>{{ 'Finance' }}</code></template>
            <template #nonMatch2><code>{{ 'MODEL_X' }}</code></template>
          </i18n-t>
          <i18n-t v-else keypath="admin.globalApplicationConfiguration.patternHelpNoRegex" tag="p" scope="global">
            <template #try><strong>{{ t('admin.globalApplicationConfiguration.tryYourPatterns') }}</strong></template>
            <template #prefix><em>{{ t('admin.globalApplicationConfiguration.modePrefix') }}</em></template>
            <template #suffix><em>{{ t('admin.globalApplicationConfiguration.modeSuffix') }}</em></template>
            <template #regex><em>{{ t('admin.globalApplicationConfiguration.modeRegex') }}</em></template>
          </i18n-t>
          <textarea v-model="sampleNames" rows="4" cols="40" :aria-label="t('admin.globalApplicationConfiguration.sampleNames')"></textarea>
          <p><button type="button" :disabled="patternTesting" @click="testPatterns">{{ t('admin.globalApplicationConfiguration.testPatterns') }}</button></p>
          <p v-if="patternTest?.error" role="alert">{{ patternTest.error }}</p>
          <template v-else-if="patternTest">
            <p v-for="(message, key) in patternTest.errors" :key="key" role="alert">{{ t('admin.globalApplicationConfiguration.patternError', { label: patternLabel(key), message }) }}</p>
            <table class="pattern-results">
              <thead><tr><th>{{ t('common.name') }}</th><th v-for="x in patterns" :key="x.key">{{ t(x.labelKey) }}</th></tr></thead>
              <tbody>
                <tr v-for="row in patternTest.rows" :key="row.name">
                  <td>{{ row.name }}</td>
                  <td v-for="x in patterns" :key="x.key">{{ row[x.key] ? t('admin.globalApplicationConfiguration.matches') : '—' }}</td>
                </tr>
              </tbody>
            </table>
          </template>
        </div>
        <p v-for="x in patterns" :key="x.key">
          <label class="field-label">
            <span class="field-label-text">{{ t('admin.globalApplicationConfiguration.patternFieldLabel', { label: t(x.labelKey) }) }}</span>
            <select v-model="config[`${x.key}Mode`]" :aria-label="t('admin.globalApplicationConfiguration.patternTypeLabel', { label: t(x.labelKey) })">
              <option value="Off">{{ t('admin.globalApplicationConfiguration.modeOff') }}</option>
              <option value="Prefix">{{ t('admin.globalApplicationConfiguration.modePrefix') }}</option>
              <option value="Suffix">{{ t('admin.globalApplicationConfiguration.modeSuffix') }}</option>
              <option value="Regex" :disabled="!config.regexSupported">{{ config.regexSupported ? t('admin.globalApplicationConfiguration.modeRegex') : t('admin.globalApplicationConfiguration.modeRegexUnavailable') }}</option>
            </select>
          </label>
          <input v-model="config[`${x.key}Value`]" :disabled="config[`${x.key}Mode`] === 'Off'" maxlength="200" :aria-label="t('admin.globalApplicationConfiguration.patternValueLabel', { label: t(x.labelKey) })" />
          <br /><small>{{ t(x.hintKey) }}</small>
        </p>
      </fieldset>
      <fieldset>
        <legend>{{ t('admin.globalApplicationConfiguration.dataFeedsLegend') }}</legend>
        <p>
          <label class="field-label"><span class="field-label-text">{{ t('admin.globalApplicationConfiguration.nightlyRunTimeLabel') }}</span> <input v-model="config.dataFeedRunTime" type="time" required /></label>
          <small>{{ t('admin.globalApplicationConfiguration.nightlyRunTimeHint') }}</small>
        </p>
        <p>
          <label class="field-label"><span class="field-label-text">{{ t('admin.globalApplicationConfiguration.runRetentionLabel') }}</span> <input v-model.number="config.dataFeedRunRetentionDays" type="number" min="1" max="365" required /></label>
        </p>
        <p>
          <label class="field-label"><span class="field-label-text">{{ t('admin.globalApplicationConfiguration.businessHoursStartLabel') }}</span> <input v-model="config.businessHoursStart" type="time" required /></label>
          <label class="field-label"><span class="field-label-text">{{ t('admin.globalApplicationConfiguration.businessHoursEndLabel') }}</span> <input v-model="config.businessHoursEnd" type="time" required /></label>
        </p>
        <p>
          <span class="field-label-text">{{ t('admin.globalApplicationConfiguration.businessDaysLabel') }}</span>
          <label v-for="day in dayNames" :key="day"><input v-model="businessDays" type="checkbox" :value="day" /> {{ t(DAY_KEYS[day]) }}</label>
          <br /><small>{{ t('admin.globalApplicationConfiguration.businessDaysHint') }}</small>
        </p>
      </fieldset>
      <p v-if="error" role="alert">{{ error }}</p>
      <button type="submit" class="btn-primary">{{ t('common.save') }}</button>
      <span v-if="saved" role="status">{{ ' ' + t('common.saved') }}</span>
    </form>
  </div>
</template>
