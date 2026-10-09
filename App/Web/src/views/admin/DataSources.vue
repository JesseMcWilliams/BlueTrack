<script setup>
// D-181 (Data Sources phase 2, Planning_Data-Sources.md): data feeds -- CSV
// files in a folder that the API imports nightly, through the same imports
// as the Bulk Actions uploads. Run now / Run all start a run in the
// background (the API answers 202, or 409 if a run is already going); this
// page then polls /status until it finishes. Inside business hours (Global
// Application Configuration) Run now asks first, since an import changes
// data people are working with.
import { ref, onMounted, onBeforeUnmount } from 'vue'
import { useRouter } from 'vue-router'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { feedTypeLabel, formatRunTime } from './dataFeedFormat'

const router = useRouter()

const feeds = ref([])
const status = ref(null)
const error = ref(null)
const message = ref(null)
const loading = ref(true)
let pollTimer = null

async function loadFeeds() {
  const response = await fetch('/api/admin/data-feeds')
  if (!response.ok) throw new Error(`Request failed: ${response.status}`)
  feeds.value = await response.json()
}

async function loadStatus() {
  const response = await fetch('/api/admin/data-feeds/status')
  if (!response.ok) throw new Error(`Request failed: ${response.status}`)
  status.value = await response.json()
}

async function load() {
  loading.value = true
  try {
    await Promise.all([loadFeeds(), loadStatus()])
    if (status.value.isRunning) startPolling()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)
onBeforeUnmount(stopPolling)

function startPolling() {
  if (pollTimer) return
  pollTimer = setInterval(async () => {
    try {
      await loadStatus()
      if (!status.value.isRunning) {
        stopPolling()
        await loadFeeds()
        message.value = 'Run finished.'
      }
    } catch (err) {
      stopPolling()
      error.value = err.message
    }
  }, 3000)
}

function stopPolling() {
  if (pollTimer) clearInterval(pollTimer)
  pollTimer = null
}

async function run(feed) {
  error.value = null
  message.value = null
  await loadStatus()
  const what = feed ? `"${feed.displayName}"` : 'all enabled feeds'
  if (status.value.withinBusinessHours &&
      !(await confirmDelete(`It is business hours. Run ${what} now? Imports change data people may be working with.`, 'Run now'))) {
    return
  }
  const response = await fetch('/api/admin/data-feeds/run', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ dataFeedKeys: feed ? [feed.dataFeedKey] : null })
  })
  if (response.status === 409) {
    const problem = await response.json().catch(() => null)
    error.value = `A run is already in progress. ${problem?.detail ?? ''}`.trim()
  } else if (!response.ok) {
    error.value = `Run failed to start: ${response.status}`
    return
  } else {
    message.value = `Started: ${what}.`
  }
  await loadStatus()
  startPolling()
}

async function remove(feed) {
  if (!(await confirmDelete(`Delete data feed "${feed.displayName}" and its run history? The file itself is not touched.`))) return
  const response = await fetch(`/api/admin/data-feeds/${feed.dataFeedKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = `Delete failed: ${response.status}`
    return
  }
  await loadFeeds()
}
</script>

<template>
  <div>
    <h2>Data Sources</h2>
    <p>
      Data feeds import a CSV file from a folder, using the same imports as the Bulk Actions uploads.
      Enabled feeds run every night, in the order shown; the newest file matching the pattern is imported and left in place.
      The application pool's account needs read access to each folder.
    </p>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <p v-if="status" class="feed-status" role="status">
        <template v-if="status.isRunning">Running{{ status.runningFeed ? `: ${status.runningFeed}` : '' }}…</template>
        <template v-else>Next nightly run: {{ formatRunTime(status.nextScheduledRun, true) }}.</template>
        <template v-if="status.withinBusinessHours"> It is business hours.</template>
      </p>
      <p v-if="message" role="status">{{ message }}</p>
      <p v-if="error" role="alert">{{ error }}</p>

      <p>
        <button type="button" class="btn-primary" @click="router.push({ name: 'admin-data-feed-create' })">+ New Feed</button>
        <button type="button" :disabled="status?.isRunning || feeds.every(f => !f.isEnabled)" @click="run(null)">Run all</button>
      </p>

      <p v-if="feeds.length === 0">No data feeds yet.</p>
      <table v-else>
        <thead>
          <tr><th>Order</th><th>Name</th><th>Import</th><th>File</th><th>Enabled</th><th>Last run</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="feed in feeds" :key="feed.dataFeedKey">
            <td>{{ feed.displayOrder }}</td>
            <td><router-link :to="{ name: 'admin-data-feed-edit', params: { dataFeedKey: feed.dataFeedKey } }">{{ feed.displayName }}</router-link></td>
            <td>{{ feedTypeLabel(feed.feedType) }}<template v-if="feed.importMappingProfileName"> ({{ feed.importMappingProfileName }})</template></td>
            <td><code>{{ feed.folderPath }}\{{ feed.fileNamePattern }}</code></td>
            <td>{{ feed.isEnabled ? 'Yes' : 'No' }}</td>
            <td>
              <template v-if="feed.lastRun">
                <strong :class="`outcome-${feed.lastRun.outcome}`">{{ feed.lastRun.outcome }}</strong>
                {{ formatRunTime(feed.lastRun.startedAt) }}
                <br /><small>{{ feed.lastRun.summary ?? feed.lastRun.errorMessage }}</small>
              </template>
              <template v-else>Never run</template>
            </td>
            <td>
              <button type="button" :disabled="status?.isRunning" @click="run(feed)">Run now</button>
              <button type="button" @click="remove(feed)">Delete</button>
            </td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>

<style scoped>
.outcome-Failed { color: var(--color-error, #b00020); }
</style>
