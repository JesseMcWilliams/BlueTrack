<script setup>
// D-234: Admin > SIEM Forwarding (ManageApplicationConfiguration). Sends
// every new audit log event to a SIEM as CEF inside RFC 5424 syslog, over TCP
// or TLS, from a background service in the API; the progress is kept in the
// database, so nothing is lost while the collector is down. Per event type:
// forwarded or not, and its CEF severity. Test sends one test event with the
// saved settings.
import { ref, computed, onMounted } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatCount } from '../../utils/formatCount'
import { formatUtcDateTime } from '../../utils/formatLocale'

const { t } = useI18n()

const loading = ref(true)
const error = ref(null)
const message = ref(null)
const testResult = ref(null)
const busy = ref(false)
const settings = ref(null)
const eventTypes = ref([])
const status = ref(null)
const defaultHostName = ref('')
const deviceVersion = ref('')

const FACILITY_KEYS = [
  'admin.siemForwarding.facility0', 'admin.siemForwarding.facility1', 'admin.siemForwarding.facility2',
  'admin.siemForwarding.facility3', 'admin.siemForwarding.facility4', 'admin.siemForwarding.facility5',
  'admin.siemForwarding.facility6', 'admin.siemForwarding.facility7', 'admin.siemForwarding.facility8',
  'admin.siemForwarding.facility9', 'admin.siemForwarding.facility10', 'admin.siemForwarding.facility11',
  'admin.siemForwarding.facility12', 'admin.siemForwarding.facility13', 'admin.siemForwarding.facility14',
  'admin.siemForwarding.facility15', 'admin.siemForwarding.facility16', 'admin.siemForwarding.facility17',
  'admin.siemForwarding.facility18', 'admin.siemForwarding.facility19', 'admin.siemForwarding.facility20',
  'admin.siemForwarding.facility21', 'admin.siemForwarding.facility22', 'admin.siemForwarding.facility23'
]

const DEFAULT_PORTS = { Tcp: 514, Tls: 6514 }

const isTls = computed(() => settings.value?.transport === 'Tls')

// Database times are UTC without a zone; shown in the viewer's time zone and format, like the Audit Log Viewer (D-294).
function utc(value) {
  return value ? formatUtcDateTime(value) : t('admin.siemForwarding.never')
}

async function problemText(response) {
  const problem = await response.json().catch(() => null)
  return problem?.detail ?? t('common.requestFailed', { status: response.status })
}

async function load() {
  loading.value = true
  error.value = null
  try {
    const response = await fetch('/api/admin/siem-forwarding')
    if (!response.ok) throw new Error(t('common.requestFailed', { status: response.status }))
    const body = await response.json()
    settings.value = { ...body.settings }
    eventTypes.value = body.eventTypes.map(type => ({ ...type }))
    status.value = body.status
    defaultHostName.value = body.defaultHostName
    deviceVersion.value = body.deviceVersion
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

// Switching transport moves the port to the new default, unless it was changed by hand.
function transportChanged(event) {
  const next = event.target.value
  const previous = next === 'Tls' ? 'Tcp' : 'Tls'
  if (Number(settings.value.port) === DEFAULT_PORTS[previous]) settings.value.port = DEFAULT_PORTS[next]
}

async function save() {
  busy.value = true
  error.value = null
  message.value = null
  testResult.value = null
  try {
    const body = {
      ...settings.value,
      port: Number(settings.value.port),
      facility: Number(settings.value.facility),
      timeoutSeconds: Number(settings.value.timeoutSeconds),
      maxMessageBytes: Number(settings.value.maxMessageBytes),
      eventTypes: eventTypes.value.map(type => ({
        auditEventTypeKey: type.auditEventTypeKey,
        isForwarded: type.isForwarded,
        cefSeverity: Number(type.cefSeverity)
      }))
    }
    const response = await fetch('/api/admin/siem-forwarding', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    })
    if (!response.ok) {
      error.value = await problemText(response)
      return
    }
    message.value = t('admin.siemForwarding.saved')
    await load()
  } finally {
    busy.value = false
  }
}

async function sendTest() {
  busy.value = true
  error.value = null
  message.value = null
  testResult.value = null
  try {
    const response = await fetch('/api/admin/siem-forwarding/test', { method: 'POST' })
    if (!response.ok) {
      error.value = await problemText(response)
      return
    }
    const body = await response.json()
    testResult.value = body.success
      ? { ok: true, text: t('admin.siemForwarding.testSent', { target: body.target }) }
      : { ok: false, text: t('admin.siemForwarding.testFailed', { target: body.target, error: body.error }) }
  } finally {
    busy.value = false
  }
}

function setAll(isForwarded) {
  for (const type of eventTypes.value) type.isForwarded = isForwarded
}

onMounted(load)
</script>

<template>
  <div>
    <h2>{{ t('admin.siemForwarding.title') }}</h2>
    <p class="intro">{{ t('admin.siemForwarding.intro') }}</p>
    <!-- UI review W10-7: with the form showing, these three sit under its Save and Test buttons, not above a long form. -->
    <template v-if="!settings">
      <p v-if="error" role="alert">{{ error }}</p>
      <p v-if="message" role="status">{{ message }}</p>
      <p v-if="testResult" :role="testResult.ok ? 'status' : 'alert'">{{ testResult.text }}</p>
    </template>
    <p v-if="loading" role="status">{{ t('common.loading') }}</p>

    <template v-else-if="settings">
      <section v-if="status" class="status">
        <h3>{{ t('admin.siemForwarding.statusHeading') }}</h3>
        <dl>
          <dt>{{ t('admin.siemForwarding.statusState') }}</dt>
          <dd>{{ settings.isEnabled ? t('admin.siemForwarding.stateOn') : t('admin.siemForwarding.stateOff') }}</dd>
          <dt>{{ t('admin.siemForwarding.statusLastSuccess') }}</dt>
          <dd>{{ utc(status.lastSuccessAt) }}</dd>
          <dt>{{ t('admin.siemForwarding.statusEventsSent') }}</dt>
          <dd>{{ formatCount(status.eventsSent) }}</dd>
          <dt>{{ t('admin.siemForwarding.statusPending') }}</dt>
          <dd>{{ formatCount(status.pendingCount) }}<template v-if="status.oldestPendingAt">{{ ' ' + t('admin.siemForwarding.oldestPending', { time: utc(status.oldestPendingAt) }) }}</template></dd>
          <template v-if="status.lastError">
            <dt>{{ t('admin.siemForwarding.statusLastError') }}</dt>
            <dd class="error-text">{{ t('admin.siemForwarding.lastError', { time: utc(status.lastErrorAt), count: formatCount(status.consecutiveFailures), error: status.lastError }) }}</dd>
          </template>
          <template v-if="status.enabledFromAuditEventKey !== null && status.enabledFromAuditEventKey !== undefined">
            <dt>{{ t('admin.siemForwarding.statusStartedAfter') }}</dt>
            <dd>{{ t('admin.siemForwarding.startedAfter', { key: status.enabledFromAuditEventKey, time: utc(status.enabledAt) }) }}</dd>
          </template>
        </dl>
      </section>

      <form @submit.prevent="save">
        <fieldset>
          <legend>{{ t('admin.siemForwarding.collector') }}</legend>
          <p><label><input v-model="settings.isEnabled" type="checkbox" /> {{ t('admin.siemForwarding.enabled') }}</label></p>
          <p class="hint">{{ t('admin.siemForwarding.enabledHint') }}</p>
          <p>
            <label class="field-label"><span class="field-label-text">{{ t('admin.siemForwarding.host') }}</span>
              <input v-model.trim="settings.host" type="text" maxlength="255" autocomplete="off" />
            </label>
          </p>
          <p>
            <label class="field-label"><span class="field-label-text">{{ t('admin.siemForwarding.transport') }}</span>
              <select v-model="settings.transport" @change="transportChanged">
                <option value="Tls">{{ t('admin.siemForwarding.transportTls') }}</option>
                <option value="Tcp">{{ t('admin.siemForwarding.transportTcp') }}</option>
              </select>
            </label>
          </p>
          <p>
            <label class="field-label"><span class="field-label-text">{{ t('admin.siemForwarding.port') }}</span>
              <input v-model="settings.port" type="number" min="1" max="65535" />
            </label>
          </p>
          <p>
            <label class="field-label"><span class="field-label-text">{{ t('admin.siemForwarding.framing') }}</span>
              <select v-model="settings.framing">
                <option value="OctetCounting">{{ t('admin.siemForwarding.framingOctetCounting') }}</option>
                <option value="LineFeed">{{ t('admin.siemForwarding.framingLineFeed') }}</option>
              </select>
            </label>
          </p>
          <p class="hint">{{ t('admin.siemForwarding.framingHint') }}</p>
        </fieldset>

        <fieldset v-if="isTls">
          <legend>{{ t('admin.siemForwarding.tls') }}</legend>
          <p>
            <label class="field-label"><span class="field-label-text">{{ t('admin.siemForwarding.trustMode') }}</span>
              <select v-model="settings.tlsTrustMode">
                <option value="System">{{ t('admin.siemForwarding.trustSystem') }}</option>
                <option value="Fingerprint">{{ t('admin.siemForwarding.trustFingerprint') }}</option>
                <option value="CaCertificate">{{ t('admin.siemForwarding.trustCa') }}</option>
              </select>
            </label>
          </p>
          <p v-if="settings.tlsTrustMode === 'Fingerprint'">
            <label class="field-label"><span class="field-label-text">{{ t('admin.siemForwarding.fingerprint') }}</span>
              <input v-model.trim="settings.tlsFingerprint" type="text" maxlength="200" autocomplete="off" />
            </label>
          </p>
          <p v-if="settings.tlsTrustMode === 'CaCertificate'">
            <label class="field-label"><span class="field-label-text">{{ t('admin.siemForwarding.caCertificate') }}</span>
              <textarea v-model="settings.tlsCaCertificate" rows="6" cols="66"></textarea>
            </label>
          </p>
          <p v-if="settings.tlsTrustMode !== 'Fingerprint'"><label><input v-model="settings.tlsCheckRevocation" type="checkbox" /> {{ t('admin.siemForwarding.checkRevocation') }}</label></p>
          <p class="hint">{{ t('admin.siemForwarding.tlsHint') }}</p>
        </fieldset>

        <fieldset>
          <legend>{{ t('admin.siemForwarding.message') }}</legend>
          <p>
            <label class="field-label"><span class="field-label-text">{{ t('admin.siemForwarding.facility') }}</span>
              <select v-model.number="settings.facility">
                <option v-for="(key, index) in FACILITY_KEYS" :key="key" :value="index">{{ t(key) }}</option>
              </select>
            </label>
          </p>
          <p>
            <label class="field-label"><span class="field-label-text">{{ t('admin.siemForwarding.syslogHostName') }}</span>
              <input v-model.trim="settings.syslogHostName" type="text" maxlength="255" :placeholder="defaultHostName" autocomplete="off" />
            </label>
          </p>
          <p>
            <label class="field-label"><span class="field-label-text">{{ t('admin.siemForwarding.appName') }}</span>
              <input v-model.trim="settings.appName" type="text" maxlength="48" />
            </label>
          </p>
          <p>
            <label class="field-label"><span class="field-label-text">{{ t('admin.siemForwarding.timeout') }}</span>
              <input v-model="settings.timeoutSeconds" type="number" min="1" max="120" />
            </label>
          </p>
          <p>
            <label class="field-label"><span class="field-label-text">{{ t('admin.siemForwarding.maxMessageBytes') }}</span>
              <input v-model="settings.maxMessageBytes" type="number" min="2048" max="65535" />
            </label>
          </p>
          <p class="hint">{{ t('admin.siemForwarding.messageHint', { version: deviceVersion }) }}</p>
        </fieldset>

        <section>
          <h3>{{ t('admin.siemForwarding.eventTypes') }}</h3>
          <p class="hint">{{ t('admin.siemForwarding.eventTypesHint') }}</p>
          <p>
            <button type="button" :disabled="busy" @click="setAll(true)">{{ t('admin.siemForwarding.forwardAll') }}</button>
            <button type="button" :disabled="busy" @click="setAll(false)">{{ t('admin.siemForwarding.forwardNone') }}</button>
          </p>
          <table>
            <thead>
              <tr>
                <th>{{ t('admin.siemForwarding.colEventType') }}</th>
                <th>{{ t('admin.siemForwarding.colDescription') }}</th>
                <th>{{ t('admin.siemForwarding.colForwarded') }}</th>
                <th>{{ t('admin.siemForwarding.colSeverity') }}</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="type in eventTypes" :key="type.auditEventTypeKey">
                <td>{{ type.eventTypeName }}</td>
                <td>{{ type.description }}</td>
                <td><input v-model="type.isForwarded" type="checkbox" :aria-label="t('admin.siemForwarding.forwardLabel', { type: type.eventTypeName })" /></td>
                <td><input v-model="type.cefSeverity" type="number" min="0" max="10" class="severity" :aria-label="t('admin.siemForwarding.severityLabel', { type: type.eventTypeName })" /></td>
              </tr>
            </tbody>
          </table>
        </section>

        <p>
          <button type="submit" class="btn-primary" :disabled="busy">{{ t('common.save') }}</button>
          <button type="button" :disabled="busy || !settings.host" @click="sendTest">{{ t('admin.siemForwarding.test') }}</button>
        </p>
        <p class="hint">{{ t('admin.siemForwarding.testHint') }}</p>
        <p v-if="error" role="alert">{{ error }}</p>
        <p v-if="message" role="status">{{ message }}</p>
        <p v-if="testResult" :role="testResult.ok ? 'status' : 'alert'">{{ testResult.text }}</p>
      </form>
    </template>
  </div>
</template>

<style scoped>
.intro,
.hint {
  max-width: 60em;
}
fieldset {
  max-width: 50em;
}
.status dl {
  display: grid;
  grid-template-columns: max-content 1fr;
  gap: 0.25rem 1rem;
}
.status dd {
  margin: 0;
}
.severity {
  width: 4em;
}
</style>
