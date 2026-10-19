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
import { useAddressState } from '../../composables/useAddressState'
import { readDate, readOneOf, readText, compactQuery } from '../../utils/listQuery'
import ReportExportButtons from '../../components/ReportExportButtons.vue'
import { useI18n } from 'vue-i18n'

const { t } = useI18n()

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

// UI review W7-2: the range, grouping, Application and Safe are in the page address (?from=...&application=7), so a
// view can be shared, repeated and reloaded. The defaults (the last 90 days, by day, no Application or Safe) are left
// out. Read before the watchers below, so a Safe named in the address isn't cleared before the choices have loaded.
const defaults = defaultRange()
useAddressState({
  sources: () => [filters.from, filters.to, filters.granularity, filters.applicationKey, filters.safeKey],
  apply(query) {
    filters.from = readDate(query, 'from', defaults.from)
    filters.to = readDate(query, 'to', defaults.to)
    filters.granularity = readOneOf(query, 'granularity', ['day', 'week'], 'day')
    filters.applicationKey = readText(query, 'application')
    filters.safeKey = readText(query, 'safe')
  },
  current: () => compactQuery(
    { from: filters.from, to: filters.to, granularity: filters.granularity, application: filters.applicationKey, safe: filters.safeKey },
    { from: defaults.from, to: defaults.to, granularity: 'day' })
})

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
  return problem?.detail ?? problem?.title ?? t('common.requestFailed', { status: response.status })
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

// UI review W7-5: a range with a missing date (a half-typed one) or From after To is not sent; the page says so and keeps the
// last report, instead of asking the API (and showing its error) on every keystroke.
const rangeError = computed(() => (filters.from && filters.to && filters.from > filters.to ? t('reports.trends.rangeBackwards') : null))
watch(queryParams, () => {
  if (!filters.from || !filters.to || rangeError.value) return
  load()
}, { immediate: true, deep: true })

// Dates and percentages in the active format locale, as counts are (D-218,
// D-227: utils/formatLocale.js -- the browser's language unless the user
// picked another).
function formatDay(value) {
  const [y, m, d] = String(value).slice(0, 10).split('-').map(Number)
  return formatShortDate(new Date(y, m - 1, d))
}

function formatPercent(value) {
  return value === null || value === undefined ? t('reports.trends.notApplicable') : formatFraction(value / 100)
}

const isWeek = computed(() => report.value?.granularity === 'week')
const periods = computed(() => report.value?.periods ?? [])
const labels = computed(() => periods.value.map(p => (isWeek.value ? t('reports.trends.weekOf', { date: formatDay(p.periodStart) }) : formatDay(p.periodStart))))
const rangeText = computed(() => periods.value.length === 0
  ? ''
  : t(isWeek.value ? 'reports.trends.rangeWeeks' : 'reports.trends.rangeDays', {
    count: formatCount(periods.value.length),
    first: labels.value[0],
    last: labels.value[labels.value.length - 1]
  }))

function baseOptions(colors, { stacked, percent }) {
  const tick = { color: colors.text }
  return {
    responsive: true,
    maintainAspectRatio: false,
    animation: false,
    interaction: { mode: 'index', intersect: false },
    plugins: {
      legend: { display: !percent, position: 'bottom', labels: { color: colors.text, boxWidth: 12 } },
      tooltip: {
        callbacks: {
          title: items => {
            const p = periods.value[items[0].dataIndex]
            return isWeek.value
              ? t('reports.trends.tooltipWeekTitle', { label: labels.value[items[0].dataIndex], date: formatDay(p.snapshotDate) })
              : labels.value[items[0].dataIndex]
          },
          label: item => t('reports.trends.tooltipLabel', { series: item.dataset.label, value: percent ? formatPercent(item.parsed.y) : formatCount(item.parsed.y) })
        }
      }
    },
    scales: {
      x: { stacked, ticks: { ...tick, maxRotation: 0, autoSkip: true }, grid: { display: false }, border: { color: colors.grid } },
      y: {
        stacked,
        beginAtZero: true,
        ...(percent ? { max: 100 } : {}),
        ticks: { ...tick, precision: 0, callback: v => (percent ? formatPercent(v) : formatCount(v)) },
        grid: { color: colors.grid },
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
    label: t('reports.trends.percentComplete'),
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
    <h2>{{ t('reports.trends.title') }}</h2>
    <p>{{ t('reports.trends.intro') }}</p>

    <p v-if="rangeError" role="alert">{{ rangeError }}</p>
    <p class="filter-row">
      <label class="field-label"><span class="field-label-text">{{ t('reports.trends.from') }}</span>
        <input v-model="filters.from" type="date" name="from" required>
      </label>
      <label class="field-label"><span class="field-label-text">{{ t('reports.trends.to') }}</span>
        <input v-model="filters.to" type="date" name="to" required>
      </label>
      <label class="field-label"><span class="field-label-text">{{ t('reports.trends.by') }}</span>
        <select v-model="filters.granularity" name="granularity">
          <option value="day">{{ t('reports.trends.day') }}</option>
          <option value="week">{{ t('reports.trends.week') }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">{{ t('reports.trends.application') }}</span>
        <select v-model="filters.applicationKey" name="applicationKey">
          <option value="">{{ t('reports.trends.all') }}</option>
          <option v-for="app in applications" :key="app.applicationKey" :value="String(app.applicationKey)">{{ app.applicationName }}</option>
        </select>
      </label>
      <label class="field-label"><span class="field-label-text">{{ t('reports.trends.safe') }}</span>
        <select v-model="filters.safeKey" name="safeKey">
          <option value="">{{ t('reports.trends.all') }}</option>
          <option v-for="safe in safeOptions" :key="safe.safeKey" :value="String(safe.safeKey)">{{ safe.safeName }} ({{ safe.sourceSystemName }})</option>
        </select>
      </label>
    </p>

    <ReportExportButtons path="/api/reports/trends/export" :params="queryParams" />

    <p v-if="loading" role="status">{{ t('common.loading') }}</p>
    <p v-else-if="error" role="alert">{{ t('reports.trends.loadFailed', { error }) }}</p>
    <p v-else-if="periods.length === 0" class="trends-empty" role="status">
      {{ filters.applicationKey !== '' || filters.safeKey !== '' ? t('reports.trends.emptyFiltered') : t('reports.trends.empty') }}
    </p>
    <template v-else>
      <p role="status">{{ rangeText }}.</p>

      <section class="trends-chart" aria-labelledby="trends-stages-title">
        <h3 id="trends-stages-title">{{ t('reports.trends.stagesTitle') }}</h3>
        <div class="trends-canvas" role="img" :aria-label="t('reports.trends.stagesChartLabel', { range: rangeText })">
          <Bar :data="stageChart" :options="stackedOptions" />
        </div>
        <button type="button" aria-controls="trends-stages-table" :aria-expanded="showTable.stages ? 'true' : 'false'" @click="showTable.stages = !showTable.stages">
          {{ showTable.stages ? t('reports.trends.hideTable') : t('reports.trends.showTable') }}
        </button>
        <table id="trends-stages-table" :class="{ 'visually-hidden': !showTable.stages }">
          <caption>{{ t('reports.trends.stagesTitle') }}</caption>
          <thead>
            <tr>
              <th scope="col">{{ isWeek ? t('reports.trends.week') : t('reports.trends.day') }}</th>
              <th v-for="s in report.stages" :key="s.name" scope="col">{{ s.name }}</th>
              <th scope="col">{{ t('reports.trends.total') }}</th>
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
        <h3 id="trends-percent-title">{{ t('reports.trends.percentComplete') }}</h3>
        <div class="trends-canvas" role="img" :aria-label="t('reports.trends.percentChartLabel', { range: rangeText })">
          <Line :data="percentChart" :options="percentOptions" />
        </div>
        <button type="button" aria-controls="trends-percent-table" :aria-expanded="showTable.percent ? 'true' : 'false'" @click="showTable.percent = !showTable.percent">
          {{ showTable.percent ? t('reports.trends.hideTable') : t('reports.trends.showTable') }}
        </button>
        <table id="trends-percent-table" :class="{ 'visually-hidden': !showTable.percent }">
          <caption>{{ t('reports.trends.percentComplete') }}</caption>
          <thead>
            <tr>
              <th scope="col">{{ isWeek ? t('reports.trends.week') : t('reports.trends.day') }}</th>
              <th scope="col">{{ t('reports.trends.compliant') }}</th>
              <th scope="col">{{ t('reports.trends.inScope') }}</th>
              <th scope="col">{{ t('reports.trends.percentComplete') }}</th>
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
        <h3 id="trends-risks-title">{{ t('reports.trends.risksTitle') }}</h3>
        <div class="trends-canvas" role="img" :aria-label="t('reports.trends.risksChartLabel', { range: rangeText })">
          <Bar :data="riskChart" :options="stackedOptions" />
        </div>
        <button type="button" aria-controls="trends-risks-table" :aria-expanded="showTable.risks ? 'true' : 'false'" @click="showTable.risks = !showTable.risks">
          {{ showTable.risks ? t('reports.trends.hideTable') : t('reports.trends.showTable') }}
        </button>
        <table id="trends-risks-table" :class="{ 'visually-hidden': !showTable.risks }">
          <caption>{{ t('reports.trends.risksTitle') }}</caption>
          <thead>
            <tr>
              <th scope="col">{{ isWeek ? t('reports.trends.week') : t('reports.trends.day') }}</th>
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
