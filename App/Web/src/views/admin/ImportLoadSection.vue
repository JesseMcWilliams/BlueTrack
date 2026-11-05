<script setup>
// D-337: the nightly SQL Agent job's steps (Import, Load, Snapshot) on Admin > Data Sources: the latest runs, the
// Load's procedures one by one, and for a run still going, the procedure it's in and what its SQL Server session is
// waiting on (dbo.usp_Get_EtlSessionActivity, signed by the installer's Db.SessionActivity). While one runs, the
// section checks back every few seconds.
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatRunTime } from './dataFeedFormat'
import { formatCount } from '../../utils/formatCount'
import { durationText, formatSeconds, procedureName } from './runProgress'

const { t } = useI18n()

const POLL_MS = 5000
const data = ref(null)
const error = ref(null)
const openRun = ref(null)
const steps = ref([])
let pollTimer = null

async function load() {
  try {
    const response = await fetch('/api/admin/etl-runs')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    data.value = await response.json()
    error.value = null
    if (openRun.value) await loadSteps(openRun.value)
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

async function toggleSteps(run) {
  if (openRun.value === run.etlRunKey) {
    openRun.value = null
    steps.value = []
    return
  }
  try {
    await loadSteps(run.etlRunKey)
    openRun.value = run.etlRunKey
  } catch (err) {
    error.value = err.message
  }
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

    <div v-for="r in running" :key="r.activity.etlRunKey" class="run-live" role="status">
      <p>
        <strong>{{ t('admin.runProgress.runningSince', { step: stepLabels[r.activity.stepName] ?? r.activity.stepName, when: formatRunTime(r.activity.startedAt), took: durationText(r.activity.startedAt, null, data.serverTime) }) }}</strong>
      </p>
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
            </td>
          </tr>
          <tr v-if="openRun === run.etlRunKey">
            <td colspan="5">
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
code { overflow-wrap: anywhere; }
</style>
