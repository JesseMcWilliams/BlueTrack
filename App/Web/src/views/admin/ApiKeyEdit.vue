<script setup>
// D-243: edit an API key in place: its name, role, expiry (renew or shorten), owner email (who is told about the
// expiry) and its own request limit. The key itself, and the tokens it has given, are unchanged; a role change
// applies to those tokens at once.
import { ref, computed, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { flash, savedMessage } from '../../composables/useFlash'
import { useI18n } from 'vue-i18n'
import { formatDate } from '../../utils/formatDate'

const { t } = useI18n()
const router = useRouter()
const props = defineProps({ apiKeyKey: { type: [String, Number], required: true } })

const loading = ref(true)
const saving = ref(false)
const loadError = ref(null)
const saveError = ref(null)
const key = ref(null)
const options = ref({ roles: [], maxLifetimeDays: 365, maxRateLimitPerMinute: 100000 })
const form = ref({ displayName: '', appRoleKey: null, expiresOn: '', ownerEmail: '', rateLimitPerMinute: null })
const originalExpiresOn = ref('')

const today = computed(() => new Date().toISOString().slice(0, 10))
// UI review W10-6: an expired key's own date is in the past, so `min=today` made the browser refuse to save it at
// all, even to rename it. The minimum applies only to a date the person has changed (an unchanged date isn't sent).
const minExpiry = computed(() => (form.value.expiresOn === originalExpiresOn.value ? undefined : today.value))
const isExpired = computed(() => !!originalExpiresOn.value && originalExpiresOn.value < today.value)
const latest = computed(() => new Date(Date.now() + options.value.maxLifetimeDays * 86400000).toISOString().slice(0, 10))

onMounted(async () => {
  try {
    const [keyResponse, optionsResponse] = await Promise.all([fetch(`/api/admin/api-keys/${props.apiKeyKey}`), fetch('/api/admin/api-keys/options')])
    if (!keyResponse.ok) throw new Error(keyResponse.status === 404 ? t('admin.apiKeyEdit.notFound') : t('common.requestFailed', { status: keyResponse.status }))
    if (!optionsResponse.ok) throw new Error(t('common.requestFailed', { status: optionsResponse.status }))
    key.value = await keyResponse.json()
    options.value = await optionsResponse.json()
    originalExpiresOn.value = key.value.expiresAt ? String(key.value.expiresAt).slice(0, 10) : '' // yyyy-mm-dd, as the date input takes it
    form.value = {
      displayName: key.value.displayName,
      appRoleKey: key.value.appRoleKey,
      expiresOn: originalExpiresOn.value,
      ownerEmail: key.value.ownerEmail ?? '',
      rateLimitPerMinute: key.value.rateLimitPerMinute ?? null
    }
  } catch (err) {
    loadError.value = err.message
  } finally {
    loading.value = false
  }
})

async function save() {
  saveError.value = null
  saving.value = true
  try {
    const response = await fetch(`/api/admin/api-keys/${props.apiKeyKey}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        displayName: form.value.displayName,
        appRoleKey: form.value.appRoleKey,
        // Only a changed date is sent, so saving never moves an expiry by accident.
        expiresOn: form.value.expiresOn && form.value.expiresOn !== originalExpiresOn.value ? form.value.expiresOn : null,
        ownerEmail: form.value.ownerEmail || null,
        rateLimitPerMinute: form.value.rateLimitPerMinute === '' ? null : form.value.rateLimitPerMinute
      })
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      saveError.value = problem?.error ?? problem?.detail ?? t('common.saveFailed', { status: response.status })
      return
    }
    flash(savedMessage(t, form.value.displayName))
    router.push({ name: 'admin-api-keys' })
  } finally {
    saving.value = false
  }
}

function cancel() {
  router.push({ name: 'admin-api-keys' })
}
</script>

<template>
  <div>
    <h2>{{ t('admin.apiKeyEdit.title') }}</h2>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="loadError" role="alert">{{ loadError }}</p>

    <form v-else @submit.prevent="save">
      <p>{{ t('admin.apiKeyEdit.keyStart') }} <code>{{ key.keyPrefix }}</code></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.apiKeyCreate.nameLabel') }}</span> <input v-model="form.displayName" maxlength="100" required /></label></p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.apiKeyCreate.roleLabel') }}</span>
          <select v-model="form.appRoleKey" required>
            <option v-for="role in options.roles" :key="role.appRoleKey" :value="role.appRoleKey">{{ role.roleName }}</option>
          </select>
        </label>
        <small>{{ t('admin.apiKeyEdit.roleHint') }}</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.apiKeyEdit.expiresLabel') }}</span> <input v-model="form.expiresOn" type="date" :min="minExpiry" :max="latest" required /></label>
        <small>{{ t('admin.apiKeyEdit.expiresHint', { max: options.maxLifetimeDays }) }}</small>
        <small v-if="isExpired" role="status">{{ ' ' + t('admin.apiKeyEdit.expiredNote', { date: formatDate(originalExpiresOn) }) }}</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.apiKeyCreate.ownerLabel') }}</span> <input v-model="form.ownerEmail" type="email" maxlength="320" /></label>
        <small>{{ t('admin.apiKeyCreate.ownerHint') }}</small>
      </p>
      <p>
        <label class="field-label"><span class="field-label-text">{{ t('admin.apiKeyCreate.rateLimitLabel') }}</span> <input v-model.number="form.rateLimitPerMinute" type="number" min="1" :max="options.maxRateLimitPerMinute" /></label>
        <small>{{ t('admin.apiKeyCreate.rateLimitHint') }}</small>
      </p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">{{ t('common.save') }}</button>
      <button type="button" @click="cancel">{{ t('common.cancel') }}</button>
    </form>
  </div>
</template>
