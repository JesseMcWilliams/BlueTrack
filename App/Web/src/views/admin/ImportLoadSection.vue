<script setup>
// D-337: the nightly SQL Agent job's steps (Import, Load, Snapshot) on Admin > Data Sources: the latest runs, the
// Load's procedures one by one, and for a run still going, the procedure it's in and what its SQL Server session is
// waiting on (dbo.usp_Get_EtlSessionActivity, signed by the installer's Db.SessionActivity). While one runs, the
// section checks back every few seconds.
// D-351: an Import lists the export files it imported (dbo.import_log) and the Self-Hosted Direct pull it checked;
// a running Import names the file it is on.
// D-348: Alert after (the stall time for the RunStalled email and the Pull and Load health check), and Cancel load,
// which stops the Import and Load SQL Agent job.
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import { useI18n } from 'vue-i18n'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { formatRunTime } from './dataFeedFormat'
import { formatCount } from '../../utils/formatCount'
import { durationText, formatSeconds, procedureName } from './runProgress'

const { t } = useI18n()

const POLL_MS = 5000
const data = ref(null)
const error = ref(null)
const openRun = ref(null)
const steps = ref([])
const importDetail = ref(null)
const message = ref(null)
const stallMinutes = ref(30)
const savingSettings = ref(false)
const cancelling = ref(false)
let pollTimer = null

async function load() {
  try {
    const response = await fetch('/api/admin/etl-runs')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    data.value = await response.json()
    if (data.value?.stallMinutes && !savingSettings.value) stallMinutes.value = data.value.stallMinutes
    error.value = null
    if (openRun.value) await loadOpen(openRun.value)
  } catch (err) {
    error.value = err.message
  }
  clearTimeout(pollTimer)
  if (data.value?.running?.length) pollTimer = setTimeout(load, POLL_MS)
}

async function loadSteps(etlRunKey) {
  const response = await fetch(`/api/admin/etl-runs/${etlRunKey}/steps`)
  if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
  steps.value = await response.json()
}

async function loadImport(etlRunKey) {
  const response = await fetch(`/api/admin/etl-runs/${etlRunKey}/import`)
  if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
  importDetail.value = await response.json()
}

// The open row is a Load (its procedures) or an Import (its files).
async function loadOpen(etlRunKey) {
  const run = (data.value?.runs ?? []).find(r => r.etlRunKey === etlRunKey)
  if (run?.stepName === 'Import') await loadImport(etlRunKey)
  else await loadSteps(etlRunKey)
}

async function toggleSteps(run) {
  if (openRun.value === run.etlRunKey) {
    openRun.value = null
    steps.value = []
    importDetail.value = null
    return
  }
  try {
    steps.value = []
    importDetail.value = null
    if (run.stepName === 'Import') await loadImport(run.etlRunKey)
    else await loadSteps(run.etlRunKey)
    openRun.value = run.etlRunKey
  } catch (err) {
    error.value = err.message
  }
}

async function saveSettings() {
  error.value = null
  message.value = null
  savingSettings.value = true
  try {
    const response = await fetch('/api/admin/etl-runs/settings', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ stallMinutes: Number(stallMinutes.value) })
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      error.value = problem?.detail ?? t('common.requestFailed', { status: response.status })
      return
    }
    message.value = t('admin.runProgress.settingsSaved')
  } finally {
    savingSettings.value = false
  }
}

async function cancelLoad() {
  error.value = null
  message.value = null
  if (!(await confirmDelete(t('admin.runProgress.confirmCancel'), t('admin.runProgress.cancelLoad')))) return
  cancelling.value = true
  let outcome = null
  let failure = null
  try {
    const response = await fetch('/api/admin/etl-runs/cancel', { method: 'POST' })
    if (response.status === 409) {
      const problem = await response.json().catch(() => null)
      failure = t('admin.runProgress.cancelRefused', { reason: problem?.detail ?? '' })
    } else if (!response.ok) {
      failure = t('common.requestFailed', { status: response.status })
    } else {
      outcome = t('admin.runProgress.cancelDone')
    }
  } finally {
    cancelling.value = false
  }
  await load()   // after the reload, which clears the section's error
  error.value = failure
  message.value = outcome
}

onMounted(load)
onBeforeUnmount(() => clearTimeout(pollTimer))

const stepLabels = computed(() => ({
  Import: t('admin.runProgress.etl_Import'),
  Load: t('admin.runProgress.etl_Load'),
  Snapshot: t('admin.runProgress.etl_Snapshot')
}))

const statusLabels = computed(() => ({
  Started: t('admin.runProgress.status_Started'),
  Succeeded: t('admin.runProgress.status_Succeeded'),
  Failed: t('admin.runProgress.status_Failed'),
  RolledBack: t('admin.runProgress.status_RolledBack')
}))

const runs = computed(() => data.value?.runs ?? [])
const running = computed(() => data.value?.running ?? [])
</script>

<template>
  <section class="import-load" aria-labelledby="import-load-heading">
    <h3 id="import-load-heading">{{ t('admin.runProgress.heading') }}</h3>
    <p>{{ t('admin.runProgress.intro') }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="message" role="status">{{ message }}</p>

    <form class="stall-settings" @submit.prevent="saveSettings">
      <label class="field-label">
        <span class="field-label-text">{{ t('admin.runProgress.stallMinutes') }}</span>
        <input v-model.number="stallMinutes" type="number" min="5" max="1440" step="1" required />
      </label>
      <button type="submit" :disabled="savingSettings">{{ t('common.save') }}</button>
      <p><small>{{ t('admin.runProgress.stallHelp') }}</small></p>
    </form>

    <p v-if="running.length">
      <button type="button" :disabled="cancelling" @click="cancelLoad">{{ t('admin.runProgress.cancelLoad') }}</button>
    </p>

    <div v-for="r in running" :key="r.activity.etlRunKey" class="run-live" role="status">
      <p>
        <strong>{{ t('admin.runProgress.runningSince', { step: stepLabels[r.activity.stepName] ?? r.activity.stepName, when: formatRunTime(r.activity.startedAt), took: durationText(r.activity.startedAt, null, data.serverTime) }) }}</strong>
      </p>
      <p v-if="r.currentFile">{{ t('admin.runProgress.currentFile', { file: r.currentFile }) }}</p>
      <p v-if="r.currentStep">
        {{ t('admin.runProgress.currentProcedure', { procedure: procedureName(r.currentStep.procName), n: formatCount(r.currentStep.stepOrder), done: formatCount(r.stepsDone), took: durationText(r.currentStep.startedAt, null, data.serverTime) }) }}
      </p>
      <template v-if="!r.activity.canSeeSessions">
        <p><small>{{ t('admin.runProgress.cannotSeeSessions') }}</small></p>
      </template>
      <template v-else-if="!r.activity.sessionFound">
        <p class="warning">{{ t('admin.runProgress.sessionEnded', { session: r.activity.sessionId ?? '?' }) }}</p>
      </template>
      <template v-else>
        <p>
          {{ t('admin.runProgress.session', { session: r.activity.sessionId, status: r.activity.requestStatus ?? t('admin.runProgress.idle'), command: r.activity.command ?? '' }) }}
          <template v-if="r.activity.currentProcedure">{{ ' ' + t('admin.runProgress.inProcedure', { procedure: procedureName(r.activity.currentProcedure) }) }}</template>
          <template v-if="r.activity.waitType">{{ ' ' + t('admin.runProgress.waiting', { wait: r.activity.waitType, took: formatSeconds((r.activity.waitMs ?? 0) / 1000) }) }}</template>
        </p>
        <p v-if="r.activity.blockingSessionId" class="warning">
          {{ t('admin.runProgress.blockedBy', { session: r.activity.blockingSessionId, login: r.activity.blockerLogin ?? '?', host: r.activity.blockerHost ?? '?', program: r.activity.blockerProgram ?? '?', database: r.activity.blockerDatabase ?? '?' }) }}
          {{ ' ' + t('admin.runProgress.blockerState', { status: r.activity.blockerStatus ?? '?', transactions: formatCount(r.activity.blockerOpenTransactions ?? 0), command: r.activity.blockerCommand ?? t('admin.runProgress.idle'), since: formatRunTime(r.activity.blockerLastRequestStart) }) }}
        </p>
        <p v-if="r.activity.currentStatement"><small>{{ t('admin.runProgress.statement') }} <code>{{ r.activity.currentStatement }}</code></small></p>
      </template>
    </div>

    <p v-if="data && runs.length === 0">{{ t('admin.runProgress.noRuns') }}</p>
    <table v-else-if="runs.length">
      <thead>
        <tr><th>{{ t('admin.runProgress.colStep') }}</th><th>{{ t('admin.runProgress.colStarted') }}</th><th>{{ t('admin.runProgress.colTook') }}</th><th>{{ t('admin.runProgress.colResult') }}</th><th></th></tr>
      </thead>
      <tbody>
        <template v-for="run in runs" :key="run.etlRunKey">
          <tr>
            <td>{{ stepLabels[run.stepName] ?? run.stepName }}</td>
            <td>{{ formatRunTime(run.startedAt) }}</td>
            <td>{{ durationText(run.startedAt, run.completedAt, data.serverTime) }}</td>
            <td>
              <strong :class="`outcome-${run.status}`">{{ statusLabels[run.status] ?? run.status }}</strong>
              <template v-if="run.errorMessage"><br /><small>{{ run.errorMessage }}<template v-if="run.errorProcedure">{{ ' ' + t('admin.runProgress.errorIn', { procedure: procedureName(run.errorProcedure), line: run.errorLine ?? '?' }) }}</template></small></template>
            </td>
            <td>
              <button v-if="run.stepCount > 0" type="button" :aria-expanded="openRun === run.etlRunKey" @click="toggleSteps(run)">
                {{ openRun === run.etlRunKey ? t('admin.runProgress.hideSteps') : t('admin.runProgress.showSteps', { n: formatCount(run.stepCount) }) }}
              </button>
              <button v-else-if="run.stepName === 'Import'" type="button" :aria-expanded="openRun === run.etlRunKey" @click="toggleSteps(run)">
                {{ openRun === run.etlRunKey ? t('admin.runProgress.hideSteps') : t('admin.runProgress.showFiles', { n: formatCount(run.fileCount ?? 0) }) }}
              </button>
            </td>
          </tr>
          <tr v-if="openRun === run.etlRunKey">
            <td v-if="importDetail" colspan="5">
              <p v-if="importDetail.pull" class="run-steps">{{ t('admin.runProgress.checkedPull', { pull: importDetail.pull.pullKey, when: formatRunTime(importDetail.pull.completedAt), status: statusLabels[importDetail.pull.status] ?? importDetail.pull.status }) }}</p>
              <p v-if="importDetail.files.length === 0" class="run-steps">{{ t('admin.runProgress.noFiles') }}</p>
              <table v-else class="run-steps">
                <thead>
                  <tr><th>{{ t('admin.runProgress.colFile') }}</th><th>{{ t('admin.runProgress.colRows') }}</th><th>{{ t('admin.runProgress.colTook') }}</th><th>{{ t('admin.runProgress.colResult') }}</th></tr>
                </thead>
                <tbody>
                  <tr v-for="f in importDetail.files" :key="`${f.sourceFileName}-${f.startedAt}`">
                    <td><code>{{ f.sourceFileName }}</code><template v-if="f.sourceSystemName"><br /><small>{{ f.sourceSystemName }}</small></template></td>
                    <td>{{ f.rowsLoaded == null ? '' : formatCount(f.rowsLoaded) }}</td>
                    <td>{{ durationText(f.startedAt, f.completedAt, data.serverTime) }}</td>
                    <td :class="`outcome-${f.status}`">{{ statusLabels[f.status] ?? f.status }}<template v-if="f.errorMessage"><br /><small>{{ f.errorMessage }}</small></template></td>
                  </tr>
                </tbody>
              </table>
            </td>
            <td v-else colspan="5">
              <table class="run-steps">
                <thead>
                  <tr><th>#</th><th>{{ t('admin.runProgress.colProcedure') }}</th><th>{{ t('admin.runProgress.colTook') }}</th><th>{{ t('admin.runProgress.colResult') }}</th></tr>
                </thead>
                <tbody>
                  <tr v-for="s in steps" :key="s.stepOrder">
                    <td>{{ s.stepOrder }}</td>
                    <td><code>{{ procedureName(s.procName) }}</code></td>
                    <td>{{ durationText(s.startedAt, s.completedAt, data.serverTime) }}</td>
                    <td :class="`outcome-${s.status}`">{{ statusLabels[s.status] ?? s.status }}</td>
                  </tr>
                </tbody>
              </table>
            </td>
          </tr>
        </template>
      </tbody>
    </table>
  </section>
</template>

<style scoped>
.run-live { border-left: 4px solid var(--color-border, #999); padding: 0.25rem 0.75rem; margin-bottom: 0.75rem; }
.run-live p { margin: 0.25rem 0; }
.warning, .outcome-Failed { color: var(--color-error-text); }
.run-steps { margin: 0.25rem 0 0.5rem 1rem; }
.stall-settings { display: flex; flex-wrap: wrap; align-items: flex-end; gap: 0.5rem; margin-bottom: 0.75rem; }
.stall-settings p { flex-basis: 100%; margin: 0; }
.stall-settings input { width: 7rem; }
code { overflow-wrap: anywhere; }
</style>
