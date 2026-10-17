<script setup>
// D-243 (phase 2): the API client mappings section of Admin > API Keys. Which role a
// caller gets when it exchanges a token from an OIDC identity provider for an API token:
// by the token's client, or by a value in its groups claim. The lowest order wins.
import { ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { formatRunTime } from './dataFeedFormat'

const { t } = useI18n()

const mappings = ref([])
const options = ref({ providers: [], roles: [], matchTypes: [] })
const error = ref(null)
const saveError = ref(null)
const saving = ref(false)
const adding = ref({ providerKey: null, matchType: 'ClientId', matchValue: '', appRoleKey: null, displayName: '', displayOrder: 100 })

// The match types are identifiers the API knows, shown with a translated label.
const MATCH_LABEL_KEYS = { ClientId: 'admin.apiClientMappings.matchClientId', Group: 'admin.apiClientMappings.matchGroup' }

async function load() {
  error.value = null
  try {
    const [list, opts] = await Promise.all([fetch('/api/admin/api-client-mappings'), fetch('/api/admin/api-client-mappings/options')])
    if (!list.ok) throw new Error(t('common.requestFailed', { status: list.status }))
    if (!opts.ok) throw new Error(t('common.requestFailed', { status: opts.status }))
    mappings.value = await list.json()
    options.value = await opts.json()
    adding.value.providerKey ??= options.value.providers[0]?.providerKey ?? null
    adding.value.appRoleKey ??= options.value.roles[0]?.appRoleKey ?? null
  } catch (err) {
    error.value = err.message
  }
}

onMounted(load)

function matchLabel(type) {
  return MATCH_LABEL_KEYS[type] ? t(MATCH_LABEL_KEYS[type]) : type
}

async function add() {
  saveError.value = null
  saving.value = true
  try {
    const response = await fetch('/api/admin/api-client-mappings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ...adding.value, displayName: adding.value.displayName || null })
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      saveError.value = problem?.error ?? problem?.detail ?? t('common.saveFailed', { status: response.status })
      return
    }
    adding.value.matchValue = ''
    adding.value.displayName = ''
    await load()
  } finally {
    saving.value = false
  }
}

async function setEnabled(mapping, enable) {
  error.value = null
  const response = await fetch(`/api/admin/api-client-mappings/${mapping.apiClientMappingKey}/${enable ? 'enable' : 'disable'}`, { method: 'POST' })
  if (!response.ok) {
    error.value = t('admin.apiKeys.actionFailed', { status: response.status })
    return
  }
  await load()
}

async function remove(mapping) {
  const what = `${matchLabel(mapping.matchType)} ${mapping.matchValue}`
  if (!(await confirmDelete(t('admin.apiClientMappings.confirmDelete', { what }), t('common.delete')))) return
  const response = await fetch(`/api/admin/api-client-mappings/${mapping.apiClientMappingKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('common.deleteFailed', { status: response.status })
    return
  }
  await load()
}
</script>

<template>
  <section class="api-client-mappings">
    <h3>{{ t('admin.apiClientMappings.title') }}</h3>
    <p>{{ t('admin.apiClientMappings.intro') }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="mappings.length === 0 && !error">{{ t('admin.apiClientMappings.noMappings') }}</p>

    <table v-else-if="mappings.length">
      <thead>
        <tr>
          <th>{{ t('admin.apiClientMappings.provider') }}</th>
          <th>{{ t('admin.apiClientMappings.match') }}</th>
          <th>{{ t('admin.apiClientMappings.value') }}</th>
          <th>{{ t('admin.apiKeys.role') }}</th>
          <th>{{ t('admin.apiClientMappings.order') }}</th>
          <th>{{ t('common.name') }}</th>
          <th>{{ t('common.status') }}</th>
          <th>{{ t('admin.apiKeys.lastUsed') }}</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="m in mappings" :key="m.apiClientMappingKey">
          <td>{{ m.providerName }}</td>
          <td>{{ matchLabel(m.matchType) }}</td>
          <td><code>{{ m.matchValue }}</code></td>
          <td>{{ m.roleName }}</td>
          <td>{{ m.displayOrder }}</td>
          <td>{{ m.displayName }}</td>
          <td>{{ m.isEnabled ? t('admin.apiKeys.statusEnabled') : t('admin.apiKeys.statusDisabled') }}</td>
          <td>{{ m.lastUsedAt ? formatRunTime(m.lastUsedAt) : t('admin.apiKeys.neverUsed') }}</td>
          <td>
            <button v-if="m.isEnabled" type="button" @click="setEnabled(m, false)">{{ t('admin.apiKeys.disable') }}</button>
            <button v-else type="button" @click="setEnabled(m, true)">{{ t('admin.apiKeys.enable') }}</button>
            <button type="button" @click="remove(m)">{{ t('common.delete') }}</button>
          </td>
        </tr>
      </tbody>
    </table>

    <p v-if="options.providers.length === 0 && !error"><small>{{ t('admin.apiClientMappings.noProviders') }}</small></p>
    <form v-else @submit.prevent="add">
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.apiClientMappings.providerLabel') }}</span>
          <select v-model="adding.providerKey" required>
            <option v-for="p in options.providers" :key="p.providerKey" :value="p.providerKey">{{ p.displayName }}</option>
          </select>
        </label>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.apiClientMappings.matchLabel') }}</span>
          <select v-model="adding.matchType" required>
            <option v-for="type in options.matchTypes" :key="type" :value="type">{{ matchLabel(type) }}</option>
          </select>
        </label>
        <small>{{ t('admin.apiClientMappings.matchHint') }}</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.apiClientMappings.valueLabel') }}</span> <input v-model="adding.matchValue" maxlength="300" required /></label>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.apiClientMappings.roleLabel') }}</span>
          <select v-model="adding.appRoleKey" required>
            <option v-for="r in options.roles" :key="r.appRoleKey" :value="r.appRoleKey">{{ r.roleName }}</option>
          </select>
        </label>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.apiClientMappings.orderLabel') }}</span> <input v-model.number="adding.displayOrder" type="number" min="0" max="10000" required /></label>
        <small>{{ t('admin.apiClientMappings.orderHint') }}</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.apiClientMappings.nameLabel') }}</span> <input v-model="adding.displayName" maxlength="100" /></label>
      </p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" :disabled="saving">{{ t('admin.apiClientMappings.addButton') }}</button>
    </form>
  </section>
</template>
