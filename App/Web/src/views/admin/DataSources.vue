<script setup>
// D-181 (Data Sources phase 2, Design_Data-Sources.md): data feeds -- CSV
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
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const router = useRouter()

const feeds = ref([])
const cyberArk = ref(null)
const status = ref(null)
const error = ref(null)
const message = ref(null)
const loading = ref(true)
let pollTimer = null

async function loadFeeds() {
  const response = await fetch('/api/admin/data-feeds')
  if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
  feeds.value = await response.json()
}

async function loadStatus() {
  const response = await fetch('/api/admin/data-feeds/status')
  if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
  status.value = await response.json()
}

// D-211: the CyberArk sources' settings, summarized; edited on their own page.
async function loadCyberArk() {
  const response = await fetch('/api/admin/cyberark-sources')
  if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
  cyberArk.value = await response.json()
}

async function load() {
  loading.value = true
  try {
    await Promise.all([loadFeeds(), loadStatus(), loadCyberArk()])
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
        message.value = t('admin.dataSources.runFinished')
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
  const question = feed
    ? t('admin.dataSources.confirmRunFeed', { name: feed.displayName })
    : t('admin.dataSources.confirmRunAll')
  if (status.value.withinBusinessHours &&
      !(await confirmDelete(question, t('admin.dataSources.runNow')))) {
    return
  }
  const response = await fetch('/api/admin/data-feeds/run', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ dataFeedKeys: feed ? [feed.dataFeedKey] : null })
  })
  if (response.status === 409) {
    const problem = await response.json().catch(() => null)
    error.value = t('admin.dataSources.alreadyRunning', { detail: problem?.detail ?? '' }).trim()
  } else if (!response.ok) {
    error.value = t('admin.dataSources.runFailedToStart', { status: response.status })
    return
  } else {
    message.value = feed ? t('admin.dataSources.startedFeed', { name: feed.displayName }) : t('admin.dataSources.startedAll')
  }
  await loadStatus()
  startPolling()
}

async function remove(feed) {
  if (!(await confirmDelete(t('admin.dataSources.confirmDelete', { name: feed.displayName })))) return
  const response = await fetch(`/api/admin/data-feeds/${feed.dataFeedKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('common.deleteFailed', { status: response.status })
    return
  }
  await loadFeeds()
}
</script>

<template>
  <div>
    <h2>{{ t('admin.dataSources.title') }}</h2>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <h3>{{ t('admin.dataSources.cyberArkHeading') }}</h3>
      <p>{{ t('admin.dataSources.cyberArkIntro') }}</p>
      <template v-if="cyberArk">
        <p v-if="!cyberArk.isConfigured" role="alert">{{ t('admin.dataSources.notConfigured') }}</p>
        <ul v-else class="cyberark-summary">
          <li>
            <i18n-t v-if="cyberArk.settings.importPrivilegeCloud" keypath="admin.dataSources.privilegeCloudOn" scope="global">
              <template #folder><code>{{ cyberArk.settings.exportFolderPath }}</code></template>
            </i18n-t>
            <template v-else>{{ t('admin.dataSources.privilegeCloudOff') }}</template>
          </li>
          <li>
            <i18n-t v-if="cyberArk.settings.importSelfHosted" keypath="admin.dataSources.selfHostedOn" scope="global">
              <template #database><code>{{ cyberArk.settings.evdDatabaseName }}</code></template>
            </i18n-t>
            <template v-else>{{ t('admin.dataSources.selfHostedOff') }}</template>
          </li>
        </ul>
      </template>
      <p><button type="button" @click="router.push({ name: 'admin-cyberark-sources-edit' })">{{ t('admin.dataSources.editCyberArk') }}</button></p>

      <h3>{{ t('admin.dataSources.dataFeedsHeading') }}</h3>
      <p>
        {{ t('admin.dataSources.dataFeedsIntro') }}
      </p>
      <p v-if="status" class="feed-status" role="status">
        <template v-if="status.isRunning">{{ status.runningFeed ? t('admin.dataSources.runningFeed', { feed: status.runningFeed }) : t('admin.dataSources.running') }}</template>
        <template v-else>{{ t('admin.dataSources.nextRun', { time: formatRunTime(status.nextScheduledRun, true) }) }}</template>
        <template v-if="status.withinBusinessHours">{{ ' ' + t('admin.dataSources.businessHours') }}</template>
      </p>
      <p v-if="message" role="status">{{ message }}</p>
      <p v-if="error" role="alert">{{ error }}</p>

      <p>
        <button type="button" class="btn-primary" @click="router.push({ name: 'admin-data-feed-create' })">{{ t('admin.dataSources.newFeed') }}</button>
        <button type="button" :disabled="status?.isRunning || feeds.every(f => !f.isEnabled)" @click="run(null)">{{ t('admin.dataSources.runAll') }}</button>
      </p>

      <p v-if="feeds.length === 0">{{ t('admin.dataSources.noFeeds') }}</p>
      <table v-else>
        <thead>
          <tr><th>{{ t('admin.dataSources.order') }}</th><th>{{ t('common.name') }}</th><th>{{ t('admin.dataSources.import') }}</th><th>{{ t('admin.dataSources.file') }}</th><th>{{ t('common.enabled') }}</th><th>{{ t('admin.dataSources.lastRun') }}</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="feed in feeds" :key="feed.dataFeedKey">
            <td>{{ feed.displayOrder }}</td>
            <td><router-link :to="{ name: 'admin-data-feed-edit', params: { dataFeedKey: feed.dataFeedKey } }">{{ feed.displayName }}</router-link></td>
            <td>{{ feedTypeLabel(feed.feedType) }}<template v-if="feed.importMappingProfileName">{{ ' ' + t('admin.dataSources.profileName', { name: feed.importMappingProfileName }) }}</template></td>
            <td><code>{{ feed.folderPath }}\{{ feed.fileNamePattern }}</code></td>
            <td>{{ feed.isEnabled ? t('admin.dataSources.yes') : t('admin.dataSources.no') }}</td>
            <td>
              <template v-if="feed.lastRun">
                <strong :class="`outcome-${feed.lastRun.outcome}`">{{ feed.lastRun.outcome }}</strong>
                {{ formatRunTime(feed.lastRun.startedAt) }}
                <br /><small>{{ feed.lastRun.summary ?? feed.lastRun.errorMessage }}</small>
              </template>
              <template v-else>{{ t('admin.dataSources.neverRun') }}</template>
            </td>
            <td>
              <button type="button" :disabled="status?.isRunning" @click="run(feed)">{{ t('admin.dataSources.runNow') }}</button>
              <button type="button" @click="remove(feed)">{{ t('common.delete') }}</button>
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
