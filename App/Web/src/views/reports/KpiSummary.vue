<script setup>
// Calls GET /api/reports/kpi-summary (ReportsController, D-143) -- five
// nested account counts (Total >= In Scope >= Onboarded >= Managed >=
// Compliant), rendered here as the four requested ratios. "In scope" means
// not currently excluded via an active Risk Accepted / Excluded exception
// (D-19/D-59); Onboarded/Managed are Blueprint stage thresholds; Compliant
// requires both the final stage and status Complete specifically.
import { ref, computed, onMounted } from 'vue'
import { formatCount } from '../../utils/formatCount'
import { formatUtcDateTime, formatPercent } from '../../utils/formatLocale'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

const summary = ref(null)
const asOf = ref(null) // UI review W7-3: when these numbers were read
const error = ref(null)
const loading = ref(true)

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/kpi-summary')
    if (!response.ok) {
      throw new Error(t('common.requestFailed', { status: response.status }))
    }
    summary.value = await response.json()
    asOf.value = new Date()
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
})

// D-294: the fraction, formatted by formatPercent in the viewer's format.
function pct(numerator, denominator) {
  if (!denominator) return null
  return numerator / denominator
}

const kpis = computed(() => {
  if (!summary.value) return []
  const s = summary.value
  return [
    { label: t('reports.kpiSummary.inScopeVsAll'), numerator: s.inScopeAccounts, denominator: s.totalAccounts, numeratorKpi: 'inScope', denominatorKpi: 'total' },
    { label: t('reports.kpiSummary.onboardedVsInScope'), numerator: s.onboardedAccounts, denominator: s.inScopeAccounts, numeratorKpi: 'onboarded', denominatorKpi: 'inScope' },
    { label: t('reports.kpiSummary.managedVsOnboarded'), numerator: s.managedAccounts, denominator: s.onboardedAccounts, numeratorKpi: 'managed', denominatorKpi: 'onboarded' },
    { label: t('reports.kpiSummary.compliantVsManaged'), numerator: s.compliantAccounts, denominator: s.managedAccounts, numeratorKpi: 'compliant', denominatorKpi: 'managed' }
  ]
})
</script>

<template>
  <div>
    <h2>{{ t('reports.kpiSummary.title') }}</h2>
    <ReportExportButtons path="/api/reports/kpi-summary/export" />
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.kpiSummary.loadFailed', { error }) }}</p>
    <template v-else>
      <table>
        <thead>
          <tr>
            <th>{{ t('reports.kpiSummary.colKpi') }}</th>
            <th>{{ t('reports.kpiSummary.colCount') }}</th>
            <th>{{ t('reports.kpiSummary.colPercentage') }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="kpi in kpis" :key="kpi.label">
            <td>{{ kpi.label }}</td>
            <!-- UI review W7-1: each count opens the accounts behind it (the Accounts list's kpi filter); zero isn't a link. -->
            <td>
              <router-link v-if="kpi.numerator > 0" :to="{ name: 'account-progress-list', query: { kpi: kpi.numeratorKpi } }">{{ formatCount(kpi.numerator) }}</router-link>
              <template v-else>{{ formatCount(kpi.numerator) }}</template>
              /
              <router-link v-if="kpi.denominator > 0" :to="{ name: 'account-progress-list', query: { kpi: kpi.denominatorKpi } }">{{ formatCount(kpi.denominator) }}</router-link>
              <template v-else>{{ formatCount(kpi.denominator) }}</template>
            </td>
            <td>{{ pct(kpi.numerator, kpi.denominator) === null ? t('reports.kpiSummary.notApplicable') : formatPercent(pct(kpi.numerator, kpi.denominator)) }}</td>
          </tr>
        </tbody>
      </table>
      <p>{{ t('reports.kpiSummary.totalAccounts', { count: formatCount(summary.totalAccounts) }) }}</p>
      <p v-if="asOf"><small>{{ t('reports.kpiSummary.asOf', { time: formatUtcDateTime(asOf.toISOString()) }) }}</small></p>
      <!-- UI review W7-3: what the words mean (D-143). -->
      <h3>{{ t('reports.kpiSummary.definitionsHeading') }}</h3>
      <dl>
        <dt>{{ t('reports.kpiSummary.defAllTerm') }}</dt><dd>{{ t('reports.kpiSummary.defAll') }}</dd>
        <dt>{{ t('reports.kpiSummary.defInScopeTerm') }}</dt><dd>{{ t('reports.kpiSummary.defInScope') }}</dd>
        <dt>{{ t('reports.kpiSummary.defOnboardedTerm') }}</dt><dd>{{ t('reports.kpiSummary.defOnboarded') }}</dd>
        <dt>{{ t('reports.kpiSummary.defManagedTerm') }}</dt><dd>{{ t('reports.kpiSummary.defManaged') }}</dd>
        <dt>{{ t('reports.kpiSummary.defCompliantTerm') }}</dt><dd>{{ t('reports.kpiSummary.defCompliant') }}</dd>
      </dl>
      <p><small>{{ t('reports.kpiSummary.listNote') }}</small></p>
    </template>
  </div>
</template>
