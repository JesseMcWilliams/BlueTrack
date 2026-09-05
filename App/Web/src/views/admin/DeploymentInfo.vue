<script setup>
// Against /api/admin/deployment (DeploymentController). Read-only:
// environment/version info, health checks (SQL Server, the active Secrets
// Store backend, identity providers), and SQL Server native backup status
// (Design_Admin_Deployment_Management.md, D-96, Part 3).
import { ref, onMounted } from 'vue'

const info = ref(null)
const error = ref(null)
const loading = ref(true)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/deployment')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    info.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)
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
      </section>
    </template>
  </div>
</template>
