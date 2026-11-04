<script setup>
// D-307: the landing page for everyone with a role. It greets the user, names their roles, shows the KPI
// funnel (D-143) to those who may see tracked data, and offers a card for each area they may open, by the
// router's own permission map (routePermissions.js), so it never offers a page that says Access Denied.
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRightsStore } from '../stores/rights'
import { canOpen, canOpenReports, canOpenAdmin } from '../router/routePermissions'
import { formatCount } from '../utils/formatCount'
import { formatPercent } from '../utils/formatLocale'

const { t } = useI18n()
const rights = useRightsStore()

const AREAS = [
  { route: 'dashboard', titleKey: 'pages.home.areas.dashboard', textKey: 'pages.home.areas.dashboardText' },
  { route: 'account-progress-list', titleKey: 'pages.home.areas.accounts', textKey: 'pages.home.areas.accountsText' },
  { route: 'risk-exceptions-list', titleKey: 'pages.home.areas.exceptions', textKey: 'pages.home.areas.exceptionsText' },
  { route: 'access-groups', titleKey: 'pages.home.areas.accessGroups', textKey: 'pages.home.areas.accessGroupsText' },
  { route: 'targets', titleKey: 'pages.home.areas.targets', textKey: 'pages.home.areas.targetsText' },
  { route: 'reports', titleKey: 'pages.home.areas.reports', textKey: 'pages.home.areas.reportsText', allowed: canOpenReports },
  { route: 'admin', titleKey: 'pages.home.areas.admin', textKey: 'pages.home.areas.adminText', allowed: canOpenAdmin }
]

const areas = computed(() => AREAS.filter(a => (a.allowed ? a.allowed(rights) : canOpen(rights, a.route))))
const showKpis = computed(() => rights.hasPermission('ViewDashboard'))

const kpiSummary = ref(null)
const kpiError = ref(null)

// Each step's share is of the step before it, as on the Dashboard's KPI card; each count opens Accounts filtered to it.
const funnel = computed(() => {
  const s = kpiSummary.value
  if (!s) return []
  const step = (kpi, labelKey, count, of) => ({ kpi, labelKey, count, share: of ? count / of : null })
  // D-326: the funnel starts with everything discovered (tracked, plus AD and DNA accounts not tracked yet).
  return [
    step(null, 'pages.home.kpiDiscovered', s.discoveredAccounts, null),
    step('total', 'pages.home.kpiAll', s.totalAccounts, s.discoveredAccounts),
    step('inScope', 'pages.home.kpiInScope', s.inScopeAccounts, s.totalAccounts),
    step('onboarded', 'pages.home.kpiOnboarded', s.onboardedAccounts, s.inScopeAccounts),
    step('managed', 'pages.home.kpiManaged', s.managedAccounts, s.onboardedAccounts),
    step('compliant', 'pages.home.kpiCompliant', s.compliantAccounts, s.managedAccounts)
  ]
})
const outOfScope = computed(() => (kpiSummary.value ? kpiSummary.value.totalAccounts - kpiSummary.value.inScopeAccounts : null))
const barWidth = (count) => {
  const total = kpiSummary.value?.discoveredAccounts
  return total ? `${Math.max(1, Math.round((count / total) * 100))}%` : '0'
}

// D-330: what needs attention, on the card it belongs to, from the same lists the Dashboard counts (D-99). Only a
// count above zero is shown, and a failed request shows nothing: the Dashboard and the worklists report failures.
const attention = ref({ overdueAtRisk: 0, overdueReview: 0, awaitingApproval: 0 })
async function countOf(url) {
  try {
    const response = await fetch(url)
    if (!response.ok) return 0
    const rows = await response.json()
    return Array.isArray(rows) ? rows.length : 0
  } catch {
    return 0
  }
}
const ATTENTION = {
  'account-progress-list': [
    { key: 'overdueAtRisk', textKey: 'pages.home.attention.overdueAtRisk', route: 'reports-overdue-worklist' }
  ],
  'risk-exceptions-list': [
    { key: 'overdueReview', textKey: 'pages.home.attention.overdueReview', route: 'risk-exceptions-overdue-worklist' },
    { key: 'awaitingApproval', textKey: 'pages.home.attention.awaitingApproval', route: 'risk-exceptions-approval-worklist' }
  ]
}
const attentionFor = (route) => (ATTENTION[route] ?? []).filter(a => attention.value[a.key] > 0)

async function loadAttention() {
  const [overdueAtRisk, overdueReview, awaitingApproval] = await Promise.all([
    countOf('/api/reports/overdue-at-risk'),
    countOf('/api/risk-exceptions/overdue-review'),
    rights.hasPermission('ApproveExceptions') ? countOf('/api/risk-exceptions/active') : 0
  ])
  attention.value = { overdueAtRisk, overdueReview, awaitingApproval }
}

onMounted(async () => {
  if (!showKpis.value) return
  loadAttention()
  try {
    const response = await fetch('/api/reports/kpi-summary')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    kpiSummary.value = await response.json()
  } catch (err) {
    kpiError.value = err.message
  }
})
</script>

<template>
  <div>
    <h1>{{ rights.displayName ? t('pages.home.welcomeName', { name: rights.displayName }) : t('pages.home.welcome') }}</h1>
    <p>
      {{ t('pages.home.yourRoles', { roles: rights.roleNames.join(', ') }) }}
      <router-link :to="{ name: 'my-profile' }">{{ t('pages.home.seeProfile') }}</router-link>
    </p>

    <section v-if="showKpis" class="home-kpis">
      <h2>{{ t('pages.home.kpiTitle') }}</h2>
      <p v-if="kpiError" role="alert">{{ kpiError }}</p>
      <p v-else-if="!kpiSummary" role="status">{{ t('common.loading') }}</p>
      <template v-else>
        <table class="home-funnel">
          <thead>
            <tr><th>{{ t('pages.home.kpiStep') }}</th><th>{{ t('pages.home.kpiAccounts') }}</th><th>{{ t('pages.home.kpiShare') }}</th><th><span class="visually-hidden">{{ t('pages.home.kpiBar') }}</span></th></tr>
          </thead>
          <tbody>
            <tr v-for="step in funnel" :key="step.labelKey">
              <td>{{ t(step.labelKey) }}</td>
              <td>
                <router-link v-if="step.kpi" :to="{ name: 'account-progress-list', query: { kpi: step.kpi } }">{{ formatCount(step.count) }}</router-link>
                <template v-else>{{ formatCount(step.count) }}</template>
              </td>
              <td>{{ step.share === null ? '' : formatPercent(step.share) }}</td>
              <td class="home-funnel-bar-cell" aria-hidden="true"><span class="home-funnel-bar" :style="{ width: barWidth(step.count) }"></span></td>
            </tr>
          </tbody>
        </table>
        <p>{{ t('pages.home.outOfScope', { count: formatCount(outOfScope) }) }}</p>
      </template>
    </section>

    <section>
      <h2>{{ t('pages.home.areasTitle') }}</h2>
      <ul class="home-areas">
        <li v-for="area in areas" :key="area.route" class="home-area">
          <h3><router-link :to="{ name: area.route }">{{ t(area.titleKey) }}</router-link></h3>
          <p>{{ t(area.textKey) }}</p>
          <p v-for="a in attentionFor(area.route)" :key="a.key" class="home-area-attention">
            <router-link :to="{ name: a.route }">{{ t(a.textKey, { count: formatCount(attention[a.key]) }, attention[a.key]) }}</router-link>
          </p>
        </li>
      </ul>
    </section>
  </div>
</template>

<style scoped>
.home-funnel td, .home-funnel th { padding-right: var(--space-3); }
.home-funnel-bar-cell { width: 16rem; }
.home-funnel-bar {
  display: block;
  height: 0.75rem;
  background: var(--color-link);
}
.home-areas {
  list-style: none;
  padding: 0;
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(16rem, 1fr));
  gap: var(--space-3);
}
.home-area {
  border: 1px solid var(--color-border);
  border-radius: 6px;
  padding: var(--space-2) var(--space-3);
}
.home-area h3 { margin: 0 0 var(--space-1); }
.home-area p { margin: 0; }
.home-area p.home-area-attention {
  margin-top: var(--space-2);
  font-weight: bold;
}
</style>
