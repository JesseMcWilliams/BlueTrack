<script setup>
// D-283: Admin > Progress Rules -- the rules the nightly load uses to move an account from one Blueprint stage to a
// later one when all of a rule's conditions match. GET/DELETE /api/admin/progress-rules (ManageProgressRules).
// Add and edit are on ProgressRuleEdit.vue.
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { confirmDelete } from '../../composables/useConfirmDialog'
import { useI18n } from 'vue-i18n'
import EmptyListMessage from '../../components/EmptyListMessage.vue'
import { describeCondition } from './progressRuleLabels'

const { t } = useI18n()
const router = useRouter()

const items = ref([])
const error = ref(null)
const loading = ref(true)

async function load() {
  loading.value = true
  try {
    const response = await fetch('/api/admin/progress-rules')
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
  if (!(await confirmDelete(t('admin.progressRules.confirmDelete', { name: item.ruleName })))) return
  const response = await fetch(`/api/admin/progress-rules/${item.progressRuleKey}`, { method: 'DELETE' })
  if (!response.ok) {
    error.value = t('common.deleteFailed', { status: response.status })
    return
  }
  await load()
}
</script>

<template>
  <div>
    <h2>{{ t('admin.progressRules.title') }}</h2>
    <p class="hint">{{ t('admin.progressRules.intro') }}</p>
    <p><router-link :to="{ name: 'admin-blueprint-stages' }">{{ t('admin.progressRules.stagesLink') }}</router-link></p>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <p><button type="button" class="btn-primary" @click="router.push({ name: 'admin-progress-rule-create' })">{{ t('admin.progressRules.newRule') }}</button></p>
      <table v-if="items.length">
        <thead>
          <tr>
            <th>{{ t('common.name') }}</th>
            <th>{{ t('admin.progressRules.colMove') }}</th>
            <th>{{ t('admin.progressRules.colWhen') }}</th>
            <th>{{ t('admin.progressRules.colEnabled') }}</th>
            <th><span class="visually-hidden">{{ t('admin.progressRules.colActions') }}</span></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="item in items" :key="item.progressRuleKey">
            <td><router-link :to="{ name: 'admin-progress-rule-edit', params: { ruleKey: item.progressRuleKey } }">{{ item.ruleName }}</router-link></td>
            <td>{{ t('admin.progressRules.move', { from: item.fromStageName, to: item.toStageName }) }}</td>
            <td>
              <ul class="conditions">
                <li v-for="(c, i) in item.conditions" :key="i">{{ describeCondition(t, c) }}</li>
              </ul>
            </td>
            <td>{{ item.isEnabled ? t('common.yes') : t('common.no') }}</td>
            <td><button type="button" @click="remove(item)">{{ t('common.delete') }}</button></td>
          </tr>
        </tbody>
      </table>
      <EmptyListMessage :show="items.length === 0" :message="t('admin.progressRules.empty')" />
    </template>
  </div>
</template>

<style scoped>
.hint {
  max-width: 60em;
}
.conditions {
  margin: 0;
  padding-left: 1.1rem;
}
</style>
