<script setup>
// D-243: Admin > API Keys. The keys scripts and other systems exchange for
// short-lived tokens. A key is shown once, when it is created (ApiKeyCreate.vue);
// this page lists them, disables and enables them, and deletes them.
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { formatDate } from '../../utils/formatDate'
import { formatRunTime } from './dataFeedFormat'
import ApiKeyReveal from '../../components/ApiKeyReveal.vue'
import ApiClientMappings from './ApiClientMappings.vue'

const { t } = useI18n()
const router = useRouter()

// An endpoint, not translated text.
const TOKEN_ENDPOINT = 'POST /api/auth/token'

const keys = ref([])
const loading = ref(true)
const error = ref(null)
const rotated = ref(null)

// SQL datetime2 reaches the page as UTC without a zone suffix.
function isExpired(key) {
  const text = /[zZ]|[+-]\d\d:\d\d$/.test(key.expiresAt) ? key.expiresAt : `${key.expiresAt}Z`
  return new Date(text).getTime() <= Date.now()
}

function statusOf(key) {
  if (isExpired(key)) return t('admin.apiKeys.statusExpired')
  return key.isEnabled ? t('admin.apiKeys.statusEnabled') : t('admin.apiKeys.statusDisabled')
}

async function load() {
  error.value = null
  try {
    const response = await fetch('/api/admin/api-keys')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    keys.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function setEnabled(key, enable) {
  error.value = null
  const response = await fetch(`/api/admin/api-keys/${key.apiKeyKey}/${enable ? 'enable' : 'disable'}`, { method: 'POST' })
  if (!response.ok) {
    error.value = t('admin.apiKeys.actionFailed', { status: response.status })
    return
  }
  await load()
}

async function rotate(key) {
  // UI review W10-4: a rotation creates a live key, so it asks first.
  if (!(await confirmDelete(t('admin.apiKeys.rotateConfirm', { name: key.displayName }), t('admin.apiKeys.rotate')))) return
  error.value = null
  const response = await fetch(`/api/admin/api-keys/${key.apiKeyKey}/rotate`, { method: 'POST' })
  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    error.value = problem?.error ?? t('admin.apiKeys.actionFailed', { status: response.status })
    return
  }
  rotated.value = await response.json()
  await load()
}

async function remove(key) {
  if (!(await confirmDelete(t('admin.apiKeys.confirmDelete', { name: key.displayName }), t('common.delete')))) return
  const response = await fetch(`/api/admin/api-keys/${key.apiKeyKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('common.deleteFailed', { status: response.status })
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>{{ t('admin.apiKeys.title') }}</h2>
    <i18n-t keypath="admin.apiKeys.intro" tag="p" scope="global">
      <template #endpoint><code>{{ TOKEN_ENDPOINT }}</code></template>
    </i18n-t>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <ApiKeyReveal v-else-if="rotated" :api-key="rotated.apiKey" :expires-at="rotated.expiresAt"
      :heading="t('admin.apiKeys.rotatedHeading', { name: rotated.displayName, from: rotated.rotatedFrom })" @done="rotated = null" />

    <template v-else>
      <p><button type="button" class="btn-primary" @click="router.push({ name: 'admin-api-key-create' })">{{ t('admin.apiKeys.newKey') }}</button></p>
      <p v-if="keys.length === 0 && !error">{{ t('admin.apiKeys.noKeys') }}</p>
      <template v-else-if="keys.length">
      <p><small>{{ t('admin.apiKeys.rotateHint') }}</small></p>

      <table>
        <thead>
          <tr>
            <th>{{ t('common.name') }}</th>
            <th>{{ t('admin.apiKeys.keyStart') }}</th>
            <th>{{ t('admin.apiKeys.role') }}</th>
            <th>{{ t('admin.apiKeys.owner') }}</th>
            <th>{{ t('admin.apiKeys.expires') }}</th>
            <th>{{ t('common.status') }}</th>
            <th>{{ t('admin.apiKeys.lastUsed') }}</th>
            <th>{{ t('admin.apiKeys.createdBy') }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="key in keys" :key="key.apiKeyKey">
            <td><router-link :to="{ name: 'admin-api-key-edit', params: { apiKeyKey: key.apiKeyKey } }">{{ key.displayName }}</router-link></td>
            <td><code>{{ key.keyPrefix }}</code></td>
            <td>{{ key.roleName }}</td>
            <td>{{ key.ownerEmail }}</td>
            <td>{{ formatDate(key.expiresAt) }}</td>
            <td>{{ statusOf(key) }}</td>
            <td>{{ key.lastUsedAt ? formatRunTime(key.lastUsedAt) : t('admin.apiKeys.neverUsed') }}<template v-if="key.lastUsedFrom">{{ ' ' + t('admin.apiKeys.usedFrom', { address: key.lastUsedFrom }) }}</template></td>
            <td>{{ key.createdByName }}</td>
            <td>
              <button v-if="key.isEnabled" type="button" @click="setEnabled(key, false)">{{ t('admin.apiKeys.disable') }}</button>
              <button v-else type="button" @click="setEnabled(key, true)">{{ t('admin.apiKeys.enable') }}</button>
              <button type="button" @click="rotate(key)">{{ t('admin.apiKeys.rotate') }}</button>
              <button type="button" @click="remove(key)">{{ t('common.delete') }}</button>
            </td>
          </tr>
        </tbody>
      </table>
      </template>

      <ApiClientMappings />
    </template>
  </div>
</template>
