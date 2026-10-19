<script setup>
// D-120: an admin-configurable set of named bands over the computed
// EffectiveRiskScore (web.account_risk_score) -- a brand-new lookup, NOT
// tied to dbo.dim_risk_level (the pre-existing, manually assigned label
// with no numeric score). Mirrors AccessGroups.vue's simpler shape (single
// add/edit/delete, no nested child collection, no bulk import).
//
// D-124 Phase 4: Add/Edit moved to its own routed page
// (RiskScoreBandEdit.vue, admin-risk-score-band-create/-edit) -- this page
// no longer owns an inline editing/startCreate/startEdit/cancelEdit form.
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { useI18n } from 'vue-i18n'
import EmptyListMessage from '../../components/EmptyListMessage.vue'

const { t } = useI18n()

const router = useRouter()

const items = ref([])
const error = ref(null)
const loading = ref(true)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/risk-score-bands')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    items.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(load)

async function remove(item) {
  if (!(await confirmDelete(t('admin.riskScoreBands.confirmDelete', { name: item.bandName })))) return
  const response = await fetch(`/api/admin/risk-score-bands/${item.riskScoreBandKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('common.deleteFailed', { status: response.status })
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>{{ t('admin.riskScoreBands.title') }}</h2>
    <p>{{ t('admin.riskScoreBands.intro') }}</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <p><button type="button" class="btn-primary" @click="router.push({ name: 'admin-risk-score-band-create' })">{{ t('admin.riskScoreBands.newBand') }}</button></p>

      <table>
        <thead>
          <tr><th>{{ t('common.name') }}</th><th>{{ t('admin.riskScoreBands.minScore') }}</th><th>{{ t('admin.riskScoreBands.maxScore') }}</th><th>{{ t('admin.riskScoreBands.order') }}</th><th></th></tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.riskScoreBandKey">
            <td><router-link :to="{ name: 'admin-risk-score-band-edit', params: { riskScoreBandKey: item.riskScoreBandKey } }">{{ item.bandName }}</router-link></td>
            <td>{{ item.minScore }}</td>
            <td>{{ item.maxScore }}</td>
            <td>{{ item.riskOrder }}</td>
            <td>
              <button @click="remove(item)">{{ t('common.delete') }}</button>
            </td>
          </tr>
        </tbody>
      </table>
      <EmptyListMessage :show="!loading && items.length === 0" :message="t('admin.riskScoreBands.emptyNone')" />
    </template>
  </div>
</template>
