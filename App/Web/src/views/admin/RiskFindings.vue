<script setup>
// D-349: Admin > Risk Score, Findings: what raises risk scores now, one row per kind of finding and target or account,
// with how many DNA rows gave it. Names only; a hard-coded credential's file and line are on Reports > DNA Hard-Coded
// Credentials (ViewDnaSensitiveColumns). Refresh rebuilds them from the DNA data now (a DNA import does it too).
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import EmptyListMessage from '../../components/EmptyListMessage.vue'
import { formatCount } from '../../utils/formatCount'

const { t } = useI18n()

const data = ref(null)
const error = ref(null)
const message = ref(null)
const loading = ref(true)
const refreshing = ref(false)
const typeFilter = ref('')

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/risk-score/findings')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    data.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function refresh() {
  error.value = null
  message.value = null
  refreshing.value = true
  try {
    const response = await fetch('/api/admin/risk-score/findings/refresh', { method: 'POST' })
    if (!response.ok) {
      error.value = t('common.requestFailed', { status: response.status })
      return
    }
    message.value = t('admin.riskFindings.refreshed')
  } finally {
    refreshing.value = false
  }
  await load()
}

const findings = computed(() => data.value?.findings ?? [])
const kinds = computed(() => [...new Map(findings.value.map(f => [f.findingType, f.findingName])).entries()]
  .map(([value, label]) => ({ value, label }))
  .sort((a, b) => a.label.localeCompare(b.label)))
const shown = computed(() => (typeFilter.value ? findings.value.filter(f => f.findingType === typeFilter.value) : findings.value))
</script>

<template>
  <div>
    <p>{{ t('admin.riskFindings.intro') }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="message" role="status">{{ message }}</p>
    <p>
      <button type="button" :disabled="refreshing" @click="refresh">{{ t('admin.riskFindings.refresh') }}</button>
    </p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else-if="data">
      <p v-if="data.total > findings.length">{{ t('admin.riskFindings.capped', { shown: formatCount(findings.length), total: formatCount(data.total) }) }}</p>
      <label v-if="kinds.length > 1" class="field-label">
        <span class="field-label-text">{{ t('admin.riskFindings.kind') }}</span>
        <select v-model="typeFilter">
          <option value="">{{ t('admin.riskFindings.allKinds') }}</option>
          <option v-for="k in kinds" :key="k.value" :value="k.value">{{ k.label }}</option>
        </select>
      </label>
      <table v-if="shown.length">
        <thead>
          <tr>
            <th>{{ t('admin.riskFindings.colFinding') }}</th>
            <th>{{ t('admin.riskFindings.colOn') }}</th>
            <th>{{ t('admin.riskFindings.colRows') }}</th>
            <th>{{ t('admin.riskFindings.colRiskValue') }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="f in shown" :key="`${f.findingType}-${f.targetKey ?? ''}-${f.accountKey ?? ''}`">
            <td>{{ f.findingName }}</td>
            <td>
              <template v-if="f.targetKey">
                {{ t('admin.riskFindings.target') }}
                <router-link :to="{ name: 'target-edit', params: { targetKey: f.targetKey } }">{{ f.targetName }}</router-link>
              </template>
              <template v-else>
                {{ t('admin.riskFindings.account') }}
                <router-link :to="{ name: 'account-progress-detail', params: { accountKey: f.accountKey } }">{{ f.userName ?? f.accountName }}</router-link>
                <template v-if="f.address">{{ ' ' + t('admin.riskFindings.at', { address: f.address }) }}</template>
                <template v-if="f.safeName"><br /><small>{{ f.safeName }}</small></template>
              </template>
            </td>
            <td>{{ formatCount(f.findingCount) }}</td>
            <td>{{ f.isEnabled ? formatCount(f.riskValue) : t('admin.riskFindings.notCounted') }}</td>
          </tr>
        </tbody>
      </table>
      <EmptyListMessage :show="shown.length === 0" :message="t('admin.riskFindings.emptyNone')" />
    </template>
  </div>
</template>
