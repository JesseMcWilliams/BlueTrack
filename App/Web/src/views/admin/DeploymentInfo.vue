<script setup>
// Against /api/admin/deployment (DeploymentController). Read-only:
// environment/version info, health checks (SQL Server, the active Secrets
// Store backend, identity providers), and SQL Server native backup status
// (Design_Admin-Deployment-Management.md, D-96, Part 3). D-117 adds a
// "Backup App" button (needs TriggerBackup, a separate permission from this
// page's own read-only ViewDeploymentInfo) -- triggers a real BACKUP
// DATABASE server-side and downloads a zip of appsettings*.json.
import { ref, onMounted } from 'vue'
import { formatCount } from '../../utils/formatCount'
import { useI18n } from 'vue-i18n'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { formatUtcDateTime } from '../../utils/formatLocale'

const { t } = useI18n()

const info = ref(null)
const error = ref(null)
const loading = ref(true)

const backupError = ref(null)
const backingUp = ref(false)
const backupNotice = ref(null) // UI review W9-7: names the file that was downloaded
// D-200: the latest backups (scheduled and manual), from web.backup_run.
const recentBackups = ref([])
// D-361: the API's latest starts (web.api_run). A gap before a start is time nothing scheduled could run.
const apiRuns = ref([])
const GAP_WORTH_SHOWING_MS = 5 * 60 * 1000

function asUtc(value) {
  return new Date(/Z|[+-]\d\d:\d\d$/.test(value) ? value : `${value}Z`)
}

function formatGap(ms) {
  const minutes = Math.round(ms / 60000)
  if (minutes < 60) return t('admin.deploymentInfo.gapMinutes', { minutes })
  const hours = Math.floor(minutes / 60)
  if (hours < 48) return t('admin.deploymentInfo.gapHours', { hours, minutes: minutes % 60 })
  return t('admin.deploymentInfo.gapDays', { days: Math.floor(hours / 24), hours: hours % 24 })
}

// What to say about the time before a start: nothing for a quick restart (an IIS recycle overlaps the old and new
// process), the length of a longer gap, and a note when the run before never stopped cleanly.
function notRunningBefore(run) {
  if (!run.previousStoppedAt) return ''
  const gap = asUtc(run.startedAt) - asUtc(run.previousStoppedAt)
  const text = gap >= GAP_WORTH_SHOWING_MS ? formatGap(gap) : ''
  if (run.previousStoppedCleanly) return text
  return text ? `${text}; ${t('admin.deploymentInfo.previousNotStopped')}` : t('admin.deploymentInfo.previousNotStopped')
}

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/deployment')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    info.value = await response.json()
    const [backups, runs] = await Promise.all([fetch('/api/admin/deployment/backups'), fetch('/api/admin/deployment/api-runs')])
    recentBackups.value = backups.ok ? await backups.json() : []
    apiRuns.value = runs.ok ? await runs.json() : []
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function backupApp() {
  // UI review W9-7: a real database backup runs on the server, so it asks first.
  if (!(await confirmDelete(t('admin.deploymentInfo.backupConfirm'), t('admin.deploymentInfo.backupApp')))) return
  backingUp.value = true
  backupError.value = null
  backupNotice.value = null
  try {
    const response = await fetch('/api/admin/deployment/backup', { method: 'POST' })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      throw new Error(problem?.detail || t('admin.deploymentInfo.backupFailed', { status: response.status }))
    }
    const blob = await response.blob()
    const disposition = response.headers.get('Content-Disposition') || ''
    const match = disposition.match(/filename="?([^"]+)"?/)
    const fileName = match ? match[1] : 'BlueTrack_ConfigBackup.zip'

    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = fileName
    link.click()
    URL.revokeObjectURL(url)

    backupNotice.value = t('admin.deploymentInfo.backupDone', { file: fileName })
    await load()
  } catch (err) {
    backupError.value = err.message
  } finally {
    backingUp.value = false
  }
}
</script>

<template>
  <div>
    <h2>{{ t('admin.deploymentInfo.title') }}</h2>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else-if="info">
      <section>
        <h3>{{ t('admin.deploymentInfo.environment') }}</h3>
        <dl>
          <dt>{{ t('admin.deploymentInfo.environmentName') }}</dt>
          <dd>{{ info.environmentName }}</dd>
          <dt>{{ t('admin.deploymentInfo.version') }}</dt>
          <dd>{{ info.version }}</dd>
          <dt>{{ t('admin.deploymentInfo.buildTimestamp') }}</dt>
          <dd>{{ info.buildTimestampUtc ?? t('admin.deploymentInfo.unknown') }}</dd>
        </dl>
      </section>

      <section>
        <h3>{{ t('admin.deploymentInfo.apiRuns') }}</h3>
        <p class="hint">{{ t('admin.deploymentInfo.apiRunsHint') }}</p>
        <p v-if="apiRuns.length === 0">{{ t('admin.deploymentInfo.noneRecorded') }}</p>
        <table v-else>
          <thead>
            <tr><th>{{ t('admin.deploymentInfo.started') }}</th><th>{{ t('admin.deploymentInfo.stopped') }}</th><th>{{ t('admin.deploymentInfo.notRunningBefore') }}</th><th>{{ t('admin.deploymentInfo.version') }}</th><th>{{ t('admin.deploymentInfo.server') }}</th><th>{{ t('admin.deploymentInfo.processId') }}</th></tr>
          </thead>
          <tbody>
            <tr v-for="(run, index) in apiRuns" :key="run.apiRunKey">
              <td>{{ formatUtcDateTime(run.startedAt) }}</td>
              <td>{{ run.stoppedAt ? formatUtcDateTime(run.stoppedAt) : (index === 0 ? t('admin.deploymentInfo.running') : t('admin.deploymentInfo.notRecorded')) }}</td>
              <td :class="{ 'status-bad': notRunningBefore(run) }"><span v-if="notRunningBefore(run)" aria-hidden="true">⚠ </span>{{ notRunningBefore(run) }}</td>
              <td>{{ run.version }}</td>
              <td>{{ run.machineName }}</td>
              <td>{{ run.processId }}</td>
            </tr>
          </tbody>
        </table>
      </section>

      <section>
        <h3>{{ t('admin.deploymentInfo.healthChecks') }}</h3>
        <table>
          <thead>
            <tr><th>{{ t('admin.deploymentInfo.component') }}</th><th>{{ t('common.status') }}</th><th>{{ t('common.description') }}</th></tr>
          </thead>
          <tbody>
            <tr v-for="check in info.healthChecks" :key="check.name">
              <td>{{ check.name }}</td>
              <!-- UI review W9-4: anything but Healthy stands out -->
              <td :class="{ 'status-bad': check.status !== 'Healthy' }"><span v-if="check.status !== 'Healthy'" aria-hidden="true">⚠ </span>{{ check.status }}</td>
              <td>{{ check.description }}</td>
            </tr>
          </tbody>
        </table>
      </section>

      <section>
        <h3>{{ t('admin.deploymentInfo.backupStatus') }}</h3>
        <button :disabled="backingUp" @click="backupApp">{{ backingUp ? t('admin.deploymentInfo.backingUp') : t('admin.deploymentInfo.backupApp') }}</button>
        <p v-if="backupError" role="alert">{{ backupError }}</p>
        <p v-if="backupNotice" role="status">{{ backupNotice }}</p>
        <p v-if="!info.backupStatus.available" role="alert">{{ info.backupStatus.error }}</p>
        <table v-else>
          <thead>
            <tr><th>{{ t('admin.deploymentInfo.backupType') }}</th><th>{{ t('admin.deploymentInfo.lastBackupFinish') }}</th></tr>
          </thead>
          <tbody>
            <tr v-for="entry in info.backupStatus.entries" :key="entry.backupType">
              <td>{{ entry.backupType }}</td>
              <td>{{ entry.lastBackupFinishDate ?? t('admin.deploymentInfo.never') }}</td>
            </tr>
          </tbody>
        </table>

        <h4>{{ t('admin.deploymentInfo.recentBackups') }}</h4>
        <p class="hint">{{ t('admin.deploymentInfo.scheduleHint') }}</p>
        <p v-if="recentBackups.length === 0">{{ t('admin.deploymentInfo.noneRecorded') }}</p>
        <table v-else>
          <thead>
            <tr><th>{{ t('admin.deploymentInfo.started') }}</th><th>{{ t('admin.deploymentInfo.type') }}</th><th>{{ t('admin.deploymentInfo.outcome') }}</th><th>{{ t('admin.deploymentInfo.file') }}</th><th>{{ t('admin.deploymentInfo.oldFilesDeleted') }}</th><th>{{ t('admin.deploymentInfo.note') }}</th></tr>
          </thead>
          <tbody>
            <tr v-for="run in recentBackups" :key="run.backupRunKey">
              <td>{{ formatUtcDateTime(run.startedAt) }}</td>
              <td>{{ run.triggerType === 'Schedule' ? t('admin.deploymentInfo.scheduled') : (run.triggeredByName ? t('admin.deploymentInfo.manualBy', { name: run.triggeredByName }) : t('admin.deploymentInfo.manual')) }}</td>
              <td :class="{ 'status-bad': run.outcome === 'Failed' }"><span v-if="run.outcome === 'Failed'" aria-hidden="true">⚠ </span>{{ run.outcome }}</td>
              <td>{{ run.filePath ?? '' }}</td>
              <td>{{ formatCount(run.filesDeleted) }}</td>
              <td>{{ run.errorMessage ?? '' }}</td>
            </tr>
          </tbody>
        </table>
      </section>
    </template>
  </div>
</template>
