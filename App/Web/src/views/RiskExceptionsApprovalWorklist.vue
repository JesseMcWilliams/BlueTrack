<script setup>
// Calls GET /api/risk-exceptions/active (RiskExceptionsController) -- every
// currently-Active exception. Requires ApproveExceptions (D-07); the API
// enforces this via a real authorization policy, but a 403 here still
// reads as a generic failure until the frontend has its own permission-
// aware routing/UI (this app doesn't check rights.permissionNames from
// /api/me before rendering yet -- a follow-up, same gap noted on the
// other permission-gated pages).
import { ref, onMounted } from 'vue'
import { formatDate } from '../utils/formatDate'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const exceptions = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/risk-exceptions/active')
    if (!response.ok) {
      throw new Error(response.status === 403
        ? t('pages.riskExceptionsApprovalWorklist.noPermission')
        : t('common.requestFailed', { status: response.status }))
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
    <h2>{{ t('pages.riskExceptionsApprovalWorklist.title') }}</h2>
    <p>{{ t('pages.riskExceptionsApprovalWorklist.intro') }}</p>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ error }}</p>
    <p v-else-if="exceptions.length === 0">{{ t('pages.riskExceptionsApprovalWorklist.none') }}</p>
    <table v-else>
      <thead>
        <tr>
          <th>{{ t('pages.riskExceptionsApprovalWorklist.colExceptionId') }}</th>
          <th>{{ t('pages.riskExceptionsApprovalWorklist.colScope') }}</th>
          <th>{{ t('pages.riskExceptionsApprovalWorklist.colJustification') }}</th>
          <th>{{ t('pages.riskExceptionsApprovalWorklist.colApprovedBy') }}</th>
          <th>{{ t('pages.riskExceptionsApprovalWorklist.colReviewDate') }}</th>
        </tr>
      </thead>
      <tbody>
        <tr
          v-for="exception in exceptions"
          :key="exception.exceptionKey"
          @click="$router.push({ name: 'risk-exception-edit', params: { exceptionKey: exception.exceptionKey } })"
        >
          <td>{{ exception.exceptionID }}</td>
          <td>{{ exception.scopeType }}: {{ exception.scopeName }}</td>
          <td>{{ exception.justification }}</td>
          <td>{{ exception.approvedByName }}</td>
          <td>{{ formatDate(exception.reviewDate) }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>
