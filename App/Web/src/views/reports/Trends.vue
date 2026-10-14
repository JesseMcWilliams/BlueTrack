<script setup>
// D-221: Reports > Trends -- GET /api/reports/trends (ViewDashboard, D-197),
// from the daily per-account snapshot. Three charts over the chosen range:
// accounts by Blueprint stage (stacked bars), % complete (one line; D-143's
// KPI rule: final stage and Complete over not Risk Accepted / Excluded) and
// accounts by risk level (stacked bars, with "Not set"). Each narrows to one
// Application and/or Safe, by Day or by Week (the week's last snapshot).
//
// Stacked bars rather than stacked areas: each period is a separate
// snapshot (a day without one is simply absent), so bars don't draw a slope
// between days nobody measured, and a part-to-whole stack per period is the
// dataviz default. Chart.js draws on a canvas; colours come from the theme
// tokens (utils/chartTheme.js) and are re-read on a theme change. Every
// chart has a text title and a data table with the same numbers -- always
// in the page for screen readers, shown on screen by its toggle.
import { ref, reactive, computed, watch, onMounted } from 'vue'
import { Bar, Line } from 'vue-chartjs'
import {
  Chart as ChartJS, BarElement, LineElement, PointElement, CategoryScale, LinearScale, Legend, Tooltip
} from 'chart.js'
import { formatCount } from '../../utils/formatCount'
import { formatShortDate, formatPercent as formatFraction } from '../../utils/formatLocale'
import { useChartTheme } from '../../utils/chartTheme'
import ReportExportButtons from '../../components/ReportExportButtons.vue'

ChartJS.register(BarElement, LineElement, PointElement, CategoryScale, LinearScale, Legend, Tooltip)

function isoDate(date) {
  const pad = n => String(n).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
}

function defaultRange() {
  const to = new Date()
  const from = new Date(to)
  from.setDate(from.getDate() - 89) // the last 90 days, today included (the API's own default)
  return { from: isoDate(from), to: isoDate(to) }
}

const filters = reactive({ ...defaultRange(), granularity: 'day', applicationKey: '', safeKey: '' })

const applications = ref([])
const safes = ref([])
const report = ref(null)
const loading = ref(true)
const error = ref(null)
const showTable = reactive({ stages: false, percent: false, risks: false })
const theme = useChartTheme()

const safeOptions = computed(() => filters.applicationKey === ''
  ? safes.value
  : safes.value.filter(s => String(s.applicationKey) === String(filters.applicationKey)))

// A Safe outside the chosen Application can't match anything: clear it.
watch(() => filters.applicationKey, () => {
  if (filters.safeKey !== '' && !safeOptions.value.some(s => String(s.safeKey) === String(filters.safeKey))) filters.safeKey = ''
})

const queryParams = computed(() => ({
  from: filters.from,
  to: filters.to,
  granularity: filters.granularity,
  applicationKey: filters.applicationKey === '' ? null : filters.applicationKey,
  safeKey: filters.safeKey === '' ? null : filters.safeKey
}))

async function problemMessage(response) {
  const problem = await response.json().catch(() => null)
  return problem?.detail ?? problem?.title ?? `Request failed: ${response.status}`
}

// Only the latest request's answer is shown, if filters change faster than the API answers.
let requestId = 0

async function load() {
  const id = ++requestId
  loading.value = true
  error.value = null
  try {
    const query = new URLSearchParams()
    for (const [key, value] of Object.entries(queryParams.value)) {
      if (value !== null && value !== '') query.set(key, String(value))
    }
    const response = await fetch(`/api/reports/trends?${query.toString()}`)
    if (!response.ok) throw new Error(await problemMessage(response))
    const body = await response.json()
    if (id === requestId) report.value = body
  } catch (err) {
    if (id !== requestId) return
    error.value = err.message
    report.value = null
  } finally {
    if (id === requestId) loading.value = false
  }
}

onMounted(async () => {
  try {
    const response = await fetch('/api/reports/trends/filter-options')
    if (response.ok) {
      const options = await response.json()
      applications.value = options.applications ?? []
      safes.value = options.safes ?? []
    }
  } catch {
    // Non-fatal: the Application and Safe filters just have no choices.
  }
})

watch(queryParams, load, { immediate: true, deep: true })

// Dates and percentages in the active format locale, as counts are (D-218,
// D-227: utils/formatLocale.js -- the browser's language unless the user
// picked another).
function formatDay(value) {
  const [y, m, d] = String(value).slice(0, 10).split('-').map(Number)
  return formatShortDate(new Date(y, m - 1, d))
}

function formatPercent(value) {
  return value === null || value === undefined ? 'N/A' : formatFraction(value / 100)
}

const isWeek = computed(() => report.value?.granularity === 'week')
const periods = computed(() => report.value?.periods ?? [])
const labels = computed(() => periods.value.map(p => (isWeek.value ? `Week of ${formatDay(p.periodStart)}` : formatDay(p.periodStart))))
const rangeText = computed(() => periods.value.length === 0
  ? ''
  : `${formatCount(periods.value.length)} ${isWeek.value ? 'week' : 'day'}(s), ${labels.value[0]} to ${labels.value[labels.value.length - 1]}`)

function baseOptions(t, { stacked, percent }) {
  const tick = { color: t.text }
  return {
    responsive: true,
    maintainAspectRatio: false,
    animation: false,
    interaction: { mode: 'index', intersect: false },
    plugins: {
      legend: { display: !percent, position: 'bottom', labels: { color: t.text, boxWidth: 12 } },
      tooltip: {
        callbacks: {
          title: items => {
            const p = periods.value[items[0].dataIndex]
            return isWeek.value ? `${labels.value[items[0].dataIndex]} (snapshot ${formatDay(p.snapshotDate)})` : labels.value[items[0].dataIndex]
          },
          label: item => `${item.dataset.label}: ${percent ? formatPercent(item.parsed.y) : formatCount(item.parsed.y)}`
        }
      }
    },
    scales: {
      x: { stacked, ticks: { ...tick, maxRotation: 0, autoSkip: true }, grid: { display: false }, border: { color: t.grid } },
      y: {
        stacked,
        beginAtZero: true,
        ...(percent ? { max: 100 } : {}),
        ticks: { ...tick, precision: 0, callback: v => (percent ? formatPercent(v) : formatCount(v)) },
        grid: { color: t.grid },
        border: { display: false }
      }
    }
  }
}

// Thin stacked segments with a 2px gap in the page colour between them.
function barDataset(label, data, color, background) {
  return { label, data, backgroundColor: color, borderColor: background, borderWidth: { top: 2 }, borderSkipped: 'bottom', maxBarThickness: 28 }
}

const stageChart = computed(() => ({
  labels: labels.value,
  datasets: (report.value?.stages ?? []).map((s, i) => barDataset(
    s.name, periods.value.map(p => p.stageCounts[i]), theme.value.stages[i % theme.value.stages.length], theme.value.background))
}))

const riskChart = computed(() => ({
  labels: labels.value,
  datasets: (report.value?.riskLevels ?? []).map((r, i) => barDataset(
    r.name, periods.value.map(p => p.riskCounts[i]),
    r.order === null ? theme.value.riskNone : theme.value.risks[i % theme.value.risks.length], theme.value.background))
}))

const percentChart = computed(() => ({
  labels: labels.value,
  datasets: [{
    label: '% complete',
    data: periods.value.map(p => p.percentComplete),
    borderColor: theme.value.line,
    backgroundColor: theme.value.line,
    borderWidth: 2,
    pointRadius: 4,
    pointHoverRadius: 6,
    spanGaps: false
  }]
}))

const stackedOptions = computed(() => baseOptions(theme.value, { stacked: true, percent: false }))
const percentOptions = computed(() => baseOptions(theme.value, { stacked: false, percent: true }))
</script>

<template>
  <div class="trends">
    <h2>Trends</h2>
    <p>
      How accounts have moved over time, from the nightly progress snapshot. % complete uses the KPI Summary's rule:
      accounts at the final stage with status Complete, out of the accounts in scope (not Risk Accepted / Excluded).
    </p>

    <p class="filter-row">
      <label class="field-label"><span class="field-label-text">From:</span>
        <input v-model="filters.from" type="date" name="from" required>
      </label>
      <label class="field-label"><span class="field-label-text">To:</span>
        <input v-model="filters.to" type="date" name="to" required>
      </label>
      <label class="field-label"><span class="field-label-text">By:</span>
        <select v-model="filters.granularity" name="granularity">
          <option value="day">Day</option>
          <option value="week">Week</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">Application:</span>
        <select v-model="filters.applicationKey" name="applicationKey">
          <option value="">All</option>
          <option v-for="app in applications" :key="app.applicationKey" :value="String(app.applicationKey)">{{ app.applicationName }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">Safe:</span>
        <select v-model="filters.safeKey" name="safeKey">
          <option value="">All</option>
          <option v-for="safe in safeOptions" :key="safe.safeKey" :value="String(safe.safeKey)">{{ safe.safeName }} ({{ safe.sourceSystemName }})</option>
        </select>
      </label>
    </p>

    <ReportExportButtons path="/api/reports/trends/export" :params="queryParams" />

    <p v-if="loading" role="status">Loading...</p>
    <p v-else-if="error" role="alert">Could not load trends: {{ error }}</p>
    <p v-else-if="periods.length === 0" class="trends-empty" role="status">
      No progress history in this range{{ filters.applicationKey !== '' || filters.safeKey !== '' ? ' for this Application or Safe' : '' }}.
      Snapshots are taken by the nightly job; choose an earlier range, or check back after it has run.
    </p>
    <template v-else>
      <p role="status">{{ rangeText }}.</p>

      <section class="trends-chart" aria-labelledby="trends-stages-title">
        <h3 id="trends-stages-title">Accounts by stage</h3>
        <div class="trends-canvas" role="img" :aria-label="`Accounts by stage, stacked bars, ${rangeText}. The numbers are in the data table that follows.`">
          <Bar :data="stageChart" :options="stackedOptions" />
        </div>
        <button type="button" aria-controls="trends-stages-table" :aria-expanded="showTable.stages ? 'true' : 'false'" @click="showTable.stages = !showTable.stages">
          {{ showTable.stages ? 'Hide data table' : 'Show data table' }}
        </button>
        <table id="trends-stages-table" :class="{ 'visually-hidden': !showTable.stages }">
          <caption>Accounts by stage</caption>
          <thead>
            <tr>
              <th scope="col">{{ isWeek ? 'Week' : 'Day' }}</th>
              <th v-for="s in report.stages" :key="s.name" scope="col">{{ s.name }}</th>
              <th scope="col">Total</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(p, i) in periods" :key="p.periodStart">
              <th scope="row">{{ labels[i] }}</th>
              <td v-for="(count, j) in p.stageCounts" :key="j">{{ formatCount(count) }}</td>
              <td>{{ formatCount(p.totalAccounts) }}</td>
            </tr>
          </tbody>
        </table>
      </section>

      <section class="trends-chart" aria-labelledby="trends-percent-title">
        <h3 id="trends-percent-title">% complete</h3>
        <div class="trends-canvas" role="img" :aria-label="`Percent complete, a line, ${rangeText}. The numbers are in the data table that follows.`">
          <Line :data="percentChart" :options="percentOptions" />
        </div>
        <button type="button" aria-controls="trends-percent-table" :aria-expanded="showTable.percent ? 'true' : 'false'" @click="showTable.percent = !showTable.percent">
          {{ showTable.percent ? 'Hide data table' : 'Show data table' }}
        </button>
        <table id="trends-percent-table" :class="{ 'visually-hidden': !showTable.percent }">
          <caption>% complete</caption>
          <thead>
            <tr>
              <th scope="col">{{ isWeek ? 'Week' : 'Day' }}</th>
              <th scope="col">Compliant</th>
              <th scope="col">In scope</th>
              <th scope="col">% complete</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(p, i) in periods" :key="p.periodStart">
              <th scope="row">{{ labels[i] }}</th>
              <td>{{ formatCount(p.compliantAccounts) }}</td>
              <td>{{ formatCount(p.inScopeAccounts) }}</td>
              <td>{{ formatPercent(p.percentComplete) }}</td>
            </tr>
          </tbody>
        </table>
      </section>

      <section class="trends-chart" aria-labelledby="trends-risks-title">
        <h3 id="trends-risks-title">Accounts by risk level</h3>
        <div class="trends-canvas" role="img" :aria-label="`Accounts by risk level, stacked bars, ${rangeText}. The numbers are in the data table that follows.`">
          <Bar :data="riskChart" :options="stackedOptions" />
        </div>
        <button type="button" aria-controls="trends-risks-table" :aria-expanded="showTable.risks ? 'true' : 'false'" @click="showTable.risks = !showTable.risks">
          {{ showTable.risks ? 'Hide data table' : 'Show data table' }}
        </button>
        <table id="trends-risks-table" :class="{ 'visually-hidden': !showTable.risks }">
          <caption>Accounts by risk level</caption>
          <thead>
            <tr>
              <th scope="col">{{ isWeek ? 'Week' : 'Day' }}</th>
              <th v-for="r in report.riskLevels" :key="r.name" scope="col">{{ r.name }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(p, i) in periods" :key="p.periodStart">
              <th scope="row">{{ labels[i] }}</th>
              <td v-for="(count, j) in p.riskCounts" :key="j">{{ formatCount(count) }}</td>
            </tr>
          </tbody>
        </table>
      </section>
    </template>
  </div>
</template>

<style scoped>
.trends-chart {
  margin-top: var(--space-5);
}
.trends-canvas {
  position: relative;
  height: 300px;
  max-width: 960px;
  margin-bottom: var(--space-2);
}
.trends-chart table {
  margin-top: var(--space-2);
}
</style>
