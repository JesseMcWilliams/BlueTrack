<script setup>
// Calls GET /api/risk-exceptions/overdue-review (RiskExceptionsController)
// -- Active exceptions past ReviewDate (D-19). Distinct from the Reports
// area's Overdue/At-Risk Worklist, which is about account progress, not
// exceptions (Design_Application-Structure.md's own note on this).
import { ref, onMounted } from 'vue'
import { formatDate } from '../utils/formatDate'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const exceptions = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/risk-exceptions/overdue-review')
    if (!response.ok) {
      throw new Error(t('common.requestFailed', { status: response.status }))
    }
    exceptions.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div>
    <h2>{{ t('pages.riskExceptionsOverdueWorklist.title') }}</h2>
    <p>{{ t('pages.riskExceptionsOverdueWorklist.intro') }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('pages.riskExceptionsOverdueWorklist.couldNotLoad', { error }) }}</p>
    <p v-else-if="exceptions.length === 0">{{ t('pages.riskExceptionsOverdueWorklist.none') }}</p>
    <table v-else>
      <thead>
        <tr>
          <th>{{ t('pages.riskExceptionsOverdueWorklist.colExceptionId') }}</th>
          <th>{{ t('pages.riskExceptionsOverdueWorklist.colScope') }}</th>
          <th>{{ t('pages.riskExceptionsOverdueWorklist.colJustification') }}</th>
          <th>{{ t('pages.riskExceptionsOverdueWorklist.colReviewDate') }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="exception in exceptions" :key="exception.exceptionKey">
          <td><router-link :to="{ name: 'risk-exception-edit', params: { exceptionKey: exception.exceptionKey } }">{{ exception.exceptionID }}</router-link></td>
          <td>{{ exception.scopeType }}: {{ exception.scopeName }}</td>
          <td>{{ exception.justification }}</td>
          <td>{{ formatDate(exception.reviewDate) }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
