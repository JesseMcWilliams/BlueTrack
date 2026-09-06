<script setup>
// D-99: analyst-facing home page (D-22) -- built from data already served
// by the Reports/Risk Exceptions pages, not new backend rollups, so this
// stays a lightweight "at a glance" summary rather than a duplicate report.
import { ref, computed, onMounted } from 'vue'
import { useRightsStore } from '../stores/rights'

const rights = useRightsStore()

const stageFunnel = ref([])
const completedCount = ref(0)
const overdueAtRiskCount = ref(null)
const overdueReviewCount = ref(null)
const activeExceptionsCount = ref(null)
const errors = ref({})
const loading = ref(true)

async function loadCount(key, url) {
  try {
    const response = await fetch(url)
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    return (await response.json()).length
  } catch (err) {
    errors.value[key] = err.message
    return null
  }
}

async function loadStageFunnel() {
  try {
    const response = await fetch('/api/reports/stage-status-summary')
    if (!response.ok) throw new Error(`Request failed: ${response.status}`)
    const rows = await response.json()
    // Zero-fills every stage (2026-09-06): the API now returns a row for
    // every (stage, status) combination, including ones with zero
    // accounts, so summing across ALL rows here naturally includes a
    // stage nobody currently occupies instead of it silently vanishing.
    const totals = new Map()
    for (const row of rows) {
      const existing = totals.get(row.stageName) ?? { stageOrder: row.stageOrder, stageName: row.stageName, accountCount: 0 }
      existing.accountCount += row.accountCount
      totals.set(row.stageName, existing)
    }
    stageFunnel.value = [...totals.values()].sort((a, b) => a.stageOrder - b.stageOrder)
    completedCount.value = rows.filter(r => r.statusName === 'Complete').reduce((sum, r) => sum + r.accountCount, 0)
  } catch (err) {
    errors.value.stageFunnel = err.message
  }
}

// Small hand-rolled SVG pie chart -- no charting library dependency,
// consistent with this app's existing "plain markup, no UI framework"
// convention. Purely a supplementary visual: the table it sits next to is
// the authoritative, accessible data source, so the chart itself is
// aria-hidden and carries no text a screen reader would need. Palette is
// a reasonable qualitative set for distinguishing slices at a glance, not
// independently contrast-verified per theme the way this app's other
// (text-carrying) colors are -- there's no text rendered on top of a
// slice for a contrast ratio to apply to.
const pieSlicePalette = ['#4e79a7', '#f28e2b', '#59a14f', '#e15759', '#b07aa1', '#76b7b2', '#edc948', '#ff9da7']

const pieSlices = computed(() => {
  const total = stageFunnel.value.reduce((sum, s) => sum + s.accountCount, 0)
  if (total === 0) return []

  let cumulativeAngle = -Math.PI / 2 // start at 12 o'clock
  const radius = 45
  const center = 50

  return stageFunnel.value
    .filter(s => s.accountCount > 0)
    .map((stage, index) => {
      const fraction = stage.accountCount / total
      const startAngle = cumulativeAngle
      const endAngle = cumulativeAngle + fraction * 2 * Math.PI
      cumulativeAngle = endAngle

      const x1 = center + radius * Math.cos(startAngle)
      const y1 = center + radius * Math.sin(startAngle)
      const x2 = center + radius * Math.cos(endAngle)
      const y2 = center + radius * Math.sin(endAngle)
      const largeArcFlag = fraction > 0.5 ? 1 : 0

      // A single 100%-share slice draws as a full circle (no gap), which
      // the standard "move-arc-close" path below can't express (the arc
      // command needs a nonzero angular span to reach a distinct end
      // point) -- drawn as two half-circle arcs instead.
      const d = fraction >= 0.999
        ? `M ${center - radius},${center} A ${radius},${radius} 0 1,1 ${center + radius},${center} A ${radius},${radius} 0 1,1 ${center - radius},${center} Z`
        : `M ${center},${center} L ${x1},${y1} A ${radius},${radius} 0 ${largeArcFlag},1 ${x2},${y2} Z`

      return { stageName: stage.stageName, accountCount: stage.accountCount, d, color: pieSlicePalette[index % pieSlicePalette.length] }
    })
})

async function load() {
  loading.value = true
  await rights.ensureLoaded()

  const tasks = [
    loadStageFunnel(),
    loadCount('overdueAtRisk', '/api/reports/overdue-at-risk').then(count => { overdueAtRiskCount.value = count }),
    loadCount('overdueReview', '/api/risk-exceptions/overdue-review').then(count => { overdueReviewCount.value = count })
  ]

  // ApproveExceptions-gated (RiskExceptionsController) -- most roles don't
  // hold it (D-94), so this card is skipped rather than attempted and 403'd.
  if (rights.hasPermission('ApproveExceptions')) {
    tasks.push(loadCount('activeExceptions', '/api/risk-exceptions/active').then(count => { activeExceptionsCount.value = count }))
  }

  await Promise.all(tasks)
  loading.value = false
}

onMounted(load)
</script>

<template>
  <div>
    <h1>Dashboard</h1>
    <p v-if="loading" role="status">Loading...</p>

    <template v-else>
      <section>
        <h2>Accounts by Stage</h2>
        <p v-if="errors.stageFunnel" role="alert">{{ errors.stageFunnel }}</p>
        <div v-else class="stage-funnel-layout">
          <div>
            <table>
              <thead>
                <tr><th>Stage</th><th>Accounts</th></tr>
              </thead>
              <tbody>
                <tr v-for="stage in stageFunnel" :key="stage.stageName">
                  <td>{{ stage.stageName }}</td>
                  <td>{{ stage.accountCount }}</td>
                </tr>
              </tbody>
            </table>
            <p>{{ completedCount }} account(s) Complete.</p>
          </div>
          <svg v-if="pieSlices.length > 0" class="stage-funnel-pie" viewBox="0 0 100 100" aria-hidden="true">
            <path v-for="slice in pieSlices" :key="slice.stageName" :d="slice.d" :fill="slice.color">
              <title>{{ slice.stageName }}: {{ slice.accountCount }}</title>
            </path>
          </svg>
        </div>
        <p><router-link :to="{ name: 'reports-stage-status-summary' }">View full stage/status breakdown</router-link></p>
      </section>

      <section>
        <h2>Overdue / At-Risk Accounts</h2>
        <p v-if="errors.overdueAtRisk" role="alert">{{ errors.overdueAtRisk }}</p>
        <p v-else>
          {{ overdueAtRiskCount }} account(s) overdue or at risk.
          <router-link :to="{ name: 'reports-overdue-worklist' }">View worklist</router-link>
        </p>
      </section>

      <section>
        <h2>Risk Exceptions Needing Attention</h2>
        <p v-if="errors.overdueReview" role="alert">{{ errors.overdueReview }}</p>
        <p v-else>
          {{ overdueReviewCount }} exception(s) past their review date.
          <router-link :to="{ name: 'risk-exceptions-overdue-worklist' }">View worklist</router-link>
        </p>

        <template v-if="rights.hasPermission('ApproveExceptions')">
          <p v-if="errors.activeExceptions" role="alert">{{ errors.activeExceptions }}</p>
          <p v-else>
            {{ activeExceptionsCount }} active exception(s) awaiting approval.
            <router-link :to="{ name: 'risk-exceptions-approval-worklist' }">View worklist</router-link>
          </p>
        </template>
      </section>
    </template>
  </div>
</template>

<style scoped>
.stage-funnel-layout {
  display: flex;
  align-items: flex-start;
  gap: var(--space-5);
  flex-wrap: wrap;
}
.stage-funnel-pie {
  width: 10em;
  height: 10em;
  flex-shrink: 0;
}
</style>
