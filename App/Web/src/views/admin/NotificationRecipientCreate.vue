<script setup>
// D-124 Phase 4 (partial conversion): only the "Add Recipient" form moves
// off Notifications.vue onto this routed page -- recipients are otherwise
// only toggled active/deleted in place on that page (no matching "Edit"
// concept to move alongside it, so none is invented here), and the SMTP
// config / notification types / test-email sections all stay exactly
// where they are (confirmed untouched, not in scope for this phase).
import { ref } from 'vue'
import { useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const router = useRouter()

const newRecipient = ref({ email: '', displayName: '', isActive: true })
const saveError = ref(null)
const saving = ref(false)

async function addRecipient() {
  saveError.value = null
  saving.value = true
  try {
    const response = await fetch('/api/admin/notifications/recipients', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(newRecipient.value)
    })
    if (!response.ok) {
      saveError.value = t('admin.notificationRecipientCreate.addFailed', { status: response.status })
      return
    }
    router.push({ name: 'admin-notifications' })
  } finally {
    saving.value = false
  }
}

function cancel() {
  router.push({ name: 'admin-notifications' })
}
</script>

<template>
  <div>
    <h2>{{ t('admin.notificationRecipientCreate.title') }}</h2>
    <form @submit.prevent="addRecipient">
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.notificationRecipientCreate.email') }}</span> <input v-model="newRecipient.email" type="email" required /></label></p>
      <p><label class="field-label"><span class="field-label-text">{{ t('admin.notificationRecipientCreate.displayName') }}</span> <input v-model="newRecipient.displayName" /></label></p>
      <p v-if="saveError" role="alert">{{ saveError }}</p>
      <button type="submit" class="btn-primary" :disabled="saving">{{ t('admin.notificationRecipientCreate.addRecipient') }}</button>
      <button type="button" @click="cancel">{{ t('common.cancel') }}</button>
    </form>
  </div>
</template>
