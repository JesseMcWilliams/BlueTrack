<script setup>
// D-243: New API Key. The key is in the response to the create and nowhere
// else (only its hash is stored), so this page shows it once and asks the
// admin to copy it before leaving.
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import ApiKeyReveal from '../../components/ApiKeyReveal.vue'

const { t } = useI18n()
const router = useRouter()

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)
const options = ref({ roles: [], defaultLifetimeDays: 90, maxLifetimeDays: 365 })
const form = ref({ displayName: '', appRoleKey: null, lifetimeDays: 90 })
const created = ref(null)

onMounted(async () => {
  try {
    const response = await fetch('/api/admin/api-keys/options')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    options.value = await response.json()
    form.value.lifetimeDays = options.value.defaultLifetimeDays
    form.value.appRoleKey = options.value.roles[0]?.appRoleKey ?? null
  } catch (err) {
    loadError.value = err.message
  } finally {
    loading.value = false
  }
})

async function create() {
  saveError.value = null
  saving.value = true
  try {
    const response = await fetch('/api/admin/api-keys', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(form.value)
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      saveError.value = problem?.error ?? problem?.detail ?? t('common.saveFailed', { status: response.status })
      return
    }
    created.value = await response.json()
  } finally {
    saving.value = false
  }
}

function backToList() {
  router.push({ name: 'admin-api-keys' })
}
</script>

<template>
  <div>
    <h2>{{ t('admin.apiKeyCreate.title') }}</h2>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <ApiKeyReveal v-else-if="created" :api-key="created.apiKey" :expires-at="created.expiresAt" :heading="t('admin.apiKeyCreate.createdHeading')" @done="backToList" />

    <form v-else @submit.prevent="create">
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.apiKeyCreate.nameLabel') }}</span> <input v-model="form.displayName" maxlength="100" required /></label>
        <small>{{ t('admin.apiKeyCreate.nameHint') }}</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.apiKeyCreate.roleLabel') }}</span>
          <select v-model="form.appRoleKey" required>
            <option v-for="role in options.roles" :key="role.appRoleKey" :value="role.appRoleKey">{{ role.roleName }}</option>
          </select>
        </label>
        <small>{{ t('admin.apiKeyCreate.roleHint') }}</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.apiKeyCreate.lifetimeLabel') }}</span> <input v-model.number="form.lifetimeDays" type="number" min="1" :max="options.maxLifetimeDays" required /></label>
        <small>{{ t('admin.apiKeyCreate.lifetimeHint', { max: options.maxLifetimeDays }) }}</small>
      </p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">{{ t('admin.apiKeyCreate.createButton') }}</button>
      <button type="button" @click="backToList">{{ t('common.cancel') }}</button>
    </form>
  </div>
</template>
