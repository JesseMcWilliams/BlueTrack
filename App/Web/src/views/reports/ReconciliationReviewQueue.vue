<script setup>
// Calls GET /api/reports/reconciliation-review-queue (ReportsController),
// backed by dbo.vw_reconciliation_review_queue -- unconfirmed cross-source
// account matches awaiting human review (D-56). Read-only: the
// confirm/reject actions (gated by ConfirmReconciliation) aren't wired up
// yet, matching this scaffold's overall maturity level.
import { ref, onMounted } from 'vue'
import { formatDate } from '../../utils/formatDate'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const items = ref([])
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/reconciliation-review-queue')
    if (!response.ok) {
      throw new Error(t('common.requestFailed', { status: response.status }))
    }
    items.value = await response.json()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div>
    <h2>{{ t('reports.reconciliationReviewQueue.title') }}</h2>
    <i18n-t keypath="reports.reconciliationReviewQueue.hint" tag="p" class="hint" scope="global">
      <template #possibleMatches><router-link :to="{ name: 'reports-possible-matches' }">{{ t('reports.reconciliationReviewQueue.hintPossibleMatches') }}</router-link></template>
    </i18n-t>
    <ReportExportButtons path="/api/reports/reconciliation-review-queue/export" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.reconciliationReviewQueue.loadFailed', { error }) }}</p>
    <p v-else-if="items.length === 0">{{ t('reports.reconciliationReviewQueue.empty') }}</p>
    <table v-else class="review-queue">
      <colgroup>
        <col style="width: 19%" />
        <col style="width: 19%" />
        <col style="width: 15%" />
        <col style="width: 8%" />
        <col style="width: 10%" />
        <col style="width: 29%" />
      </colgroup>
      <thead>
        <tr>
          <th>{{ t('reports.reconciliationReviewQueue.colSelfHosted') }}</th>
          <th>{{ t('reports.reconciliationReviewQueue.colPrivCloud') }}</th>
          <th>{{ t('reports.reconciliationReviewQueue.colMatchMethod') }}</th>
          <th>{{ t('reports.reconciliationReviewQueue.colConfidence') }}</th>
          <th>{{ t('reports.reconciliationReviewQueue.colMatchedDate') }}</th>
          <th>{{ t('reports.reconciliationReviewQueue.colNotes') }}</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="item in items" :key="item.reconciliationKey">
          <td>{{ item.selfHostedAccountName }} ({{ item.selfHostedUserName }}@{{ item.selfHostedAddress }})</td>
          <td>{{ item.privCloudAccountName }} ({{ item.privCloudUserName }}@{{ item.privCloudAddress }})</td>
          <td>{{ item.matchMethod }}</td>
          <td>{{ item.matchConfidence }}</td>
          <td>{{ formatDate(item.matchedDate) }}</td>
          <td>{{ item.notes }}</td>
        </tr>
      </tbody>
    </table>
  </div>
</template>

<style scoped>
/* Content-density pass, 2026-09-06: default table auto-layout sizes each
   column to its longest unbreakable content -- the two Account columns'
   identifier strings have few natural break points, so they were
   claiming more width than they need while Notes (which has plenty of
   break points but the most actual text) was squeezed into a narrow
   column that wrapped word-by-word, making every row far taller than
   necessary. Explicit widths give Notes the room its content actually
   needs instead of leaving it to emergent auto-layout behavior. */
.review-queue {
  table-layout: fixed;
  width: 100%;
}
.review-queue td {
  overflow-wrap: break-word;
}
</style>
