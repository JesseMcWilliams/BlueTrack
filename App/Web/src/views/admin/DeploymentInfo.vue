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

const info = ref(null)
const error = ref(null)
const loading = ref(true)

const backupError = ref(null)
const backingUp = ref(false)
// D-200: the latest backups (scheduled and manual), from web.backup_run.
const recentBackups = ref([])

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/deployment')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    info.value = await response.json()
    const backups = await fetch('/api/admin/deployment/backups')
    recentBackups.value = backups.ok ? await backups.json() : []
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function backupApp() {
  backingUp.value = true
  backupError.value = null
  try {
    const response = await fetch('/api/admin/deployment/backup', { method: 'POST' })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      throw new Error(problem?.detail || `Backup failed: ${response.status}`)
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
    <h2>Deployment</h2>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else-if="info">
      <section>
        <h3>Environment</h3>
        <dl>
          <dt>Environment name</dt>
          <dd>{{ info.environmentName }}</dd>
          <dt>Version</dt>
          <dd>{{ info.version }}</dd>
          <dt>Build timestamp (UTC)</dt>
          <dd>{{ info.buildTimestampUtc ?? 'Unknown' }}</dd>
        </dl>
      </section>

      <section>
        <h3>Health Checks</h3>
        <table>
          <thead>
            <tr><th>Component</th><th>Status</th><th>Description</th></tr>
          </thead>
          <tbody>
            <tr v-for="check in info.healthChecks" :key="check.name">
              <td>{{ check.name }}</td>
              <td>{{ check.status }}</td>
              <td>{{ check.description }}</td>
            </tr>
          </tbody>
        </table>
      </section>

      <section>
        <h3>SQL Server Backup Status</h3>
        <button :disabled="backingUp" @click="backupApp">{{ backingUp ? 'Backing up...' : 'Backup App' }}</button>
        <p v-if="backupError" role="alert">{{ backupError }}</p>
        <p v-if="!info.backupStatus.available" role="alert">{{ info.backupStatus.error }}</p>
        <table v-else>
          <thead>
            <tr><th>Backup Type</th><th>Last Backup Finish Date</th></tr>
          </thead>
          <tbody>
            <tr v-for="entry in info.backupStatus.entries" :key="entry.backupType">
              <td>{{ entry.backupType }}</td>
              <td>{{ entry.lastBackupFinishDate ?? 'Never' }}</td>
            </tr>
          </tbody>
        </table>

        <h4>Recent backups</h4>
        <p class="hint">Scheduled backups are set on Global Application Configuration (time and days to keep).</p>
        <p v-if="recentBackups.length === 0">None recorded yet.</p>
        <table v-else>
          <thead>
            <tr><th>Started (UTC)</th><th>Type</th><th>Outcome</th><th>File</th><th>Old files deleted</th><th>Note</th></tr>
          </thead>
          <tbody>
            <tr v-for="run in recentBackups" :key="run.backupRunKey">
              <td>{{ run.startedAt.replace('T', ' ').slice(0, 19) }}</td>
              <td>{{ run.triggerType === 'Schedule' ? 'Scheduled' : `Manual${run.triggeredByName ? ` (${run.triggeredByName})` : ''}` }}</td>
              <td>{{ run.outcome }}</td>
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
