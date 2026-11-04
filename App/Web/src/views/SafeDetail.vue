<script setup>
// D-308: one Safe's page, opened from the Safe on an account or on the Accounts list. It shows the BlueTrack
// application mapped to the Safe (D-31), whether the decommission or ignore patterns name it (D-186), and its first
// accounts, with a link to all of them on Accounts (the list's own query, filtered by Safe).
import { ref, computed, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRightsStore } from '../stores/rights'
import { formatCount } from '../utils/formatCount'
import { formatDate } from '../utils/formatDate'

const props = defineProps({ safeKey: { type: [String, Number], required: true } })
const { t } = useI18n()
const rights = useRightsStore()

const ACCOUNTS_SHOWN = 50

const safe = ref(null)
const accounts = ref([])
const accountCount = ref(0)
const loading = ref(true)
const error = ref(null)
const notFound = ref(false)

const canMapApplication = computed(() => rights.hasPermission('CurateApplicationMapping'))

async function load() {
  loading.value = true
  error.value = null
  notFound.value = false
  try {
    const response = await fetch(`/api/safes/${encodeURIComponent(props.safeKey)}`)
    if (response.status === 404) {
      notFound.value = true
      return
    }
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    safe.value = await response.json()

    const params = new URLSearchParams({ safe: String(props.safeKey), sort: 'userName:asc', page: '1', pageSize: String(ACCOUNTS_SHOWN) })
    const list = await fetch(`/api/account-progress?${params.toString()}`)
    if (!list.ok) throw new Error(t('common.requestFailed', { status: list.status }))
    accountCount.value = Number(list.headers.get('X-Filtered-Count') ?? 0)
    accounts.value = await list.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

watch(() => props.safeKey, load, { immediate: true })
</script>

<template>
  <div>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="notFound" role="alert">{{ t('pages.safeDetail.notFound') }}</p>
    <p v-else-if="error" role="alert">{{ t('pages.safeDetail.loadFailed', { error }) }}</p>
    <template v-else-if="safe">
      <h1>{{ safe.safeName }}</h1>
      <p v-if="safe.isDecomFlagged" class="safe-note">{{ t('pages.safeDetail.flagged') }}</p>
      <p v-if="safe.isIgnored" class="safe-note">{{ t('pages.safeDetail.ignored') }}</p>
      <p v-if="safe.isInternal" class="safe-note">{{ t('pages.safeDetail.internal') }}</p>

      <dl class="safe-facts">
        <dt>{{ t('pages.safeDetail.application') }}</dt>
        <dd v-if="safe.applicationKey">
          {{ t('pages.safeDetail.applicationValue', { name: safe.applicationName, code: safe.applicationCode }) }}
          <template v-if="safe.applicationOwnerName"><br />{{ t('pages.safeDetail.applicationOwner', { owner: safe.applicationOwnerName }) }}</template>
        </dd>
        <dd v-else>
          {{ t('pages.safeDetail.noApplication') }}
          <router-link v-if="canMapApplication" :to="{ name: 'admin-application-mapping' }">{{ t('pages.safeDetail.mapApplication') }}</router-link>
        </dd>
        <template v-if="safe.description"><dt>{{ t('pages.safeDetail.description') }}</dt><dd>{{ safe.description }}</dd></template>
        <template v-if="safe.sourceSystemName"><dt>{{ t('pages.safeDetail.source') }}</dt><dd>{{ safe.sourceSystemName }}</dd></template>
        <template v-if="safe.managingCPM"><dt>{{ t('pages.safeDetail.managingCpm') }}</dt><dd>{{ safe.managingCPM }}</dd></template>
        <template v-if="safe.createdDate"><dt>{{ t('pages.safeDetail.created') }}</dt><dd>{{ formatDate(safe.createdDate) }}</dd></template>
      </dl>

      <section>
        <h2>{{ t('pages.safeDetail.accountsTitle') }}</h2>
        <p>{{ t('pages.safeDetail.accountCounts', { active: formatCount(safe.activeAccountCount), deleted: formatCount(safe.deletedAccountCount) }) }}</p>
        <p v-if="accounts.length === 0">{{ t('pages.safeDetail.noAccounts') }}</p>
        <template v-else>
          <table>
            <thead>
              <tr>
                <th>{{ t('pages.safeDetail.colUsername') }}</th><th>{{ t('pages.safeDetail.colAddress') }}</th>
                <th>{{ t('pages.safeDetail.colStage') }}</th><th>{{ t('pages.safeDetail.colStatus') }}</th><th>{{ t('pages.safeDetail.colOwner') }}</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="account in accounts" :key="account.accountKey">
                <td><router-link :to="{ name: 'account-progress-detail', params: { accountKey: account.accountKey } }">{{ account.userName || account.accountName }}</router-link></td>
                <td>{{ account.address }}</td>
                <td>{{ account.stageName }}</td>
                <td>{{ account.statusName }}</td>
                <td>{{ account.ownerName }}</td>
              </tr>
            </tbody>
          </table>
          <p>
            <template v-if="accountCount > accounts.length">{{ t('pages.safeDetail.showingFirst', { shown: formatCount(accounts.length), count: formatCount(accountCount) }) + ' ' }}</template>
            <router-link :to="{ name: 'account-progress-list', query: { safe: String(safe.safeKey) } }">{{ t('pages.safeDetail.showAll') }}</router-link>
          </p>
        </template>
      </section>
    </template>
  </div>
</template>

<style scoped>
.safe-facts {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: var(--space-1) var(--space-3);
}
.safe-facts dt { font-weight: bold; }
.safe-facts dd { margin: 0; }
</style>
