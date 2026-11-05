<script setup>
// D-349: Admin > Risk Score, Defaults: the score a target made from an account's address gets, the starting score of
// a new target and a new access group, and each kind of finding's risk value (0-1000) and whether it counts. A changed
// finding value marks every score to recalculate (Recalculate Now on Reports > Risk Score, or the nightly load).
import { ref, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const form = ref(null)
const error = ref(null)
const message = ref(null)
const saving = ref(false)

async function load() {
  try {
    const response = await fetch('/api/admin/risk-score/defaults')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    form.value = await response.json()
  } catch (err) {
    error.value = err.message
  }
}

onMounted(load)

async function save() {
  error.value = null
  message.value = null
  saving.value = true
  try {
    const response = await fetch('/api/admin/risk-score/defaults', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        defaultAddressTargetRiskScore: Number(form.value.defaultAddressTargetRiskScore),
        defaultTargetRiskScore: Number(form.value.defaultTargetRiskScore),
        defaultAccessGroupRiskScore: Number(form.value.defaultAccessGroupRiskScore),
        findingTypes: form.value.findingTypes.map(f => ({ findingType: f.findingType, riskValue: Number(f.riskValue), isEnabled: f.isEnabled }))
      })
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null)
      error.value = problem?.detail ?? t('common.requestFailed', { status: response.status })
      return
    }
    const body = await response.json()
    form.value = body.defaults
    message.value = body.rescore ? t('admin.riskScoreDefaults.savedRescore') : t('common.saved')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div>
    <p>{{ t('admin.riskScoreDefaults.intro') }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="message" role="status">{{ message }}</p>
    <p v-if="!form && !error" role="status">{{ t('common.loading') }}</p>

    <form v-if="form" @submit.prevent="save">
      <h3>{{ t('admin.riskScoreDefaults.scoresHeading') }}</h3>
      <label class="field-label">
        <span class="field-label-text">{{ t('admin.riskScoreDefaults.addressTarget') }}</span>
        <input v-model.number="form.defaultAddressTargetRiskScore" type="number" min="0" max="1000" step="1" required />
      </label>
      <p><small>{{ t('admin.riskScoreDefaults.addressTargetHelp') }}</small></p>
      <label class="field-label">
        <span class="field-label-text">{{ t('admin.riskScoreDefaults.newTarget') }}</span>
        <input v-model.number="form.defaultTargetRiskScore" type="number" min="0" max="1000" step="1" required />
      </label>
      <label class="field-label">
        <span class="field-label-text">{{ t('admin.riskScoreDefaults.newAccessGroup') }}</span>
        <input v-model.number="form.defaultAccessGroupRiskScore" type="number" min="0" max="1000" step="1" required />
      </label>
      <p><small>{{ t('admin.riskScoreDefaults.newHelp') }}</small></p>

      <h3>{{ t('admin.riskScoreDefaults.findingsHeading') }}</h3>
      <p>{{ t('admin.riskScoreDefaults.findingsIntro') }}</p>
      <table>
        <thead>
          <tr><th>{{ t('admin.riskScoreDefaults.colFinding') }}</th><th>{{ t('admin.riskScoreDefaults.colRiskValue') }}</th><th>{{ t('admin.riskScoreDefaults.colCounts') }}</th></tr>
        </thead>
        <tbody>
          <tr v-for="f in form.findingTypes" :key="f.findingType">
            <td><strong>{{ f.displayName }}</strong><br /><small>{{ f.description }}</small></td>
            <td><input v-model.number="f.riskValue" type="number" min="0" max="1000" step="1" required :aria-label="t('admin.riskScoreDefaults.riskValueFor', { name: f.displayName })" /></td>
            <td><input v-model="f.isEnabled" type="checkbox" :aria-label="t('admin.riskScoreDefaults.countsFor', { name: f.displayName })" /></td>
          </tr>
        </tbody>
      </table>
      <p><button type="submit" class="btn-primary" :disabled="saving">{{ t('common.save') }}</button></p>
    </form>
  </div>
</template>

<style scoped>
input[type="number"] { width: 7rem; }
</style>
