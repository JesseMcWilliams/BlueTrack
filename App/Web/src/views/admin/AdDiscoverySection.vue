<script setup>
// D-317: AD Account Discovery as a data source on Admin > Data Sources: which LDAP domains it reads (set on
// Credentials & LDAP), when it runs, Run now, and its recent runs. While a run is going, it checks back every few
// seconds.
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatRunTime } from './dataFeedFormat'
import { formatCount } from '../../utils/formatCount'
import { problemMessage } from '../../utils/problemText'

const { t } = useI18n()

const POLL_MS = 3000
const status = ref(null)
const runTime = ref('')
const error = ref(null)
const notice = ref(null)
const saving = ref(false)
let pollTimer = null

async function load() {
  try {
    const response = await fetch('/api/admin/ad-discovery')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    status.value = await response.json()
    if (!runTime.value) runTime.value = status.value.runTime
  } catch (err) {
    error.value = err.message
  }
  clearTimeout(pollTimer)
  if (status.value?.isRunning) pollTimer = setTimeout(load, POLL_MS)
}

// D-324: the domains discovery reads (enabled, and used as a data source); each can be turned on or off here.
const readCount = computed(() => status.value?.domains.filter(d => d.isEnabled && d.useAsDataSource).length ?? 0)

async function setDataSource(domain, useAsDataSource) {
  error.value = null
  notice.value = null
  const response = await fetch(`/api/admin/ad-discovery/domains/${domain.ldapConfigKey}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ useAsDataSource })
  })
  if (!response.ok) {
    error.value = await problemMessage(response, t('common.requestFailed', { status: response.status }))
    return
  }
  await load()
}

async function saveRunTime() {
  saving.value = true
  error.value = null
  notice.value = null
  try {
    const response = await fetch('/api/admin/ad-discovery/settings', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ runTime: runTime.value })
    })
    if (!response.ok) throw new Error(await problemMessage(response, t('common.requestFailed', { status: response.status })))
    notice.value = t('admin.adDiscovery.saved')
    await load()
  } catch (err) {
    error.value = err.message
  } finally {
    saving.value = false
  }
}

async function runNow() {
  error.value = null
  notice.value = null
  const response = await fetch('/api/admin/ad-discovery/run', { method: 'POST' })
  if (!response.ok) {
    error.value = await problemMessage(response, t('common.requestFailed', { status: response.status }))
    return
  }
  notice.value = t('admin.adDiscovery.runStarted')
  await load()
}

onMounted(load)
onBeforeUnmount(() => clearTimeout(pollTimer))
</script>

<template>
  <section class="ad-discovery">
    <h3>{{ t('admin.adDiscovery.heading') }}</h3>
    <p>{{ t('admin.adDiscovery.intro') }}</p>
    <template v-if="status">
      <p>{{ t('admin.adDiscovery.domains', { count: formatCount(readCount) }, readCount) }}</p>
      <ul v-if="status.domains.length" class="ad-domains">
        <li v-for="domain in status.domains" :key="domain.ldapConfigKey">
          <label><input type="checkbox" :checked="domain.useAsDataSource" @change="setDataSource(domain, $event.target.checked)" />
            {{ domain.domainName }}</label>
          <small v-if="!domain.isEnabled">{{ ' ' + t('admin.adDiscovery.domainDisabled') }}</small>
        </li>
      </ul>
      <p>
        <router-link :to="{ name: 'admin-credentials', query: { newDomain: '1' } }">{{ t('admin.adDiscovery.addDomain') }}</router-link>
        |
        <router-link :to="{ name: 'admin-credentials' }">{{ t('admin.adDiscovery.domainsLink') }}</router-link>
      </p>
      <form class="filter-row" @submit.prevent="saveRunTime">
        <label class="field-label"><span class="field-label-text">{{ t('admin.adDiscovery.runTime') }}</span>
          <input v-model="runTime" type="time" required /></label>
        <button type="submit" :disabled="saving">{{ t('common.save') }}</button>
        <button type="button" :disabled="status.isRunning" @click="runNow">{{ t('admin.adDiscovery.runNow') }}</button>
      </form>
      <p v-if="status.isRunning" role="status">{{ t('admin.adDiscovery.running') }}</p>
    </template>
    <p v-if="notice" role="status">{{ notice }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <template v-if="status">
      <p v-if="status.recentRuns.length === 0">{{ t('admin.adDiscovery.noRuns') }}</p>
      <table v-else>
        <thead>
          <tr><th>{{ t('admin.adDiscovery.started') }}</th><th>{{ t('admin.adDiscovery.by') }}</th><th>{{ t('admin.adDiscovery.result') }}</th><th>{{ t('admin.adDiscovery.found') }}</th><th>{{ t('admin.adDiscovery.message') }}</th></tr>
        </thead>
        <tbody>
          <tr v-for="run in status.recentRuns" :key="run.adDiscoveryRunKey">
            <td>{{ formatRunTime(run.startedAt) }}</td>
            <td>{{ run.triggeredBy }}</td>
            <td><strong :class="`outcome-${run.status}`">{{ run.status }}</strong></td>
            <td><template v-if="run.domainCount">{{ t('admin.adDiscovery.foundValue', { accounts: formatCount(run.accountsFound), read: formatCount(run.domainsRead), domains: formatCount(run.domainCount) }) }}</template></td>
            <td>{{ run.message }}</td>
          </tr>
        </tbody>
      </table>
    </template>
  </section>
</template>

<style scoped>
.outcome-Failed { color: var(--color-error-text); }
.ad-domains { list-style: none; padding-left: 0; }
</style>
