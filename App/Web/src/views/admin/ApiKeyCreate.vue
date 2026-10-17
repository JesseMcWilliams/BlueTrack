<script setup>
// D-243: New API Key. The key is in the response to the create and nowhere
// else (only its hash is stored), so this page shows it once and asks the
// admin to copy it before leaving.
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import { formatDate } from '../../utils/formatDate'

const { t } = useI18n()
const router = useRouter()

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)
const options = ref({ roles: [], defaultLifetimeDays: 90, maxLifetimeDays: 365 })
const form = ref({ displayName: '', appRoleKey: null, lifetimeDays: 90 })
const created = ref(null)
const copied = ref(false)

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

async function copyKey() {
  try {
    await navigator.clipboard.writeText(created.value.apiKey)
    copied.value = true
  } catch {
    copied.value = false   // the key is selectable on the page; copying by hand still works
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

    <section v-else-if="created">
      <p role="status"><strong>{{ t('admin.apiKeyCreate.createdHeading') }}</strong></p>
      <p>{{ t('admin.apiKeyCreate.copyWarning') }}</p>
      <p><input class="api-key-value" :value="created.apiKey" readonly :aria-label="t('admin.apiKeyCreate.keyLabel')" @focus="$event.target.select()" /></p>
      <p>
        <button type="button" @click="copyKey">{{ t('admin.apiKeyCreate.copy') }}</button>
        <span v-if="copied" role="status">{{ ' ' + t('admin.apiKeyCreate.copied') }}</span>
      </p>
      <p>{{ t('admin.apiKeyCreate.expiresOn', { date: formatDate(created.expiresAt) }) }}</p>
      <button type="button" class="btn-primary" @click="backToList">{{ t('admin.apiKeyCreate.done') }}</button>
    </section>

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

<style scoped>
.api-key-value {
  width: 100%;
  max-width: 40rem;
  font-family: monospace;
}
</style>
