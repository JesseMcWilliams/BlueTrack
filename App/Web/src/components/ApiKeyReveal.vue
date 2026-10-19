<script setup>
// D-243: a new API key, shown once. The key is in the response to the create
// (or the rotate) and nowhere else (only its hash is stored), so whoever
// creates it must copy it before leaving the page.
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { formatDate } from '../utils/formatDate'

const { t } = useI18n()

const props = defineProps({
  apiKey: { type: String, required: true },
  expiresAt: { type: String, required: true },
  heading: { type: String, required: true }
})
const emit = defineEmits(['done'])

const copied = ref(false)
const copyFailed = ref(false) // UI review W10-5: copying can be refused (a plain-HTTP host has no clipboard)

async function copyKey() {
  try {
    await navigator.clipboard.writeText(props.apiKey)
    copied.value = true
    copyFailed.value = false
  } catch {
    copied.value = false   // the key is selectable on the page; copying by hand still works
    copyFailed.value = true
  }
}
</script>

<template>
  <section>
    <p role="status"><strong>{{ heading }}</strong></p>
    <p>{{ t('admin.apiKeyCreate.copyWarning') }}</p>
    <p><input class="api-key-value" :value="apiKey" readonly :aria-label="t('admin.apiKeyCreate.keyLabel')" @focus="$event.target.select()" /></p>
    <p>
      <button type="button" @click="copyKey">{{ t('admin.apiKeyCreate.copy') }}</button>
      <span v-if="copied" role="status">{{ ' ' + t('admin.apiKeyCreate.copied') }}</span>
      <span v-if="copyFailed" role="alert">{{ ' ' + t('admin.apiKeyCreate.copyFailed') }}</span>
    </p>
    <p>{{ t('admin.apiKeyCreate.expiresOn', { date: formatDate(expiresAt) }) }}</p>
    <button type="button" class="btn-primary" @click="emit('done')">{{ t('admin.apiKeyCreate.done') }}</button>
  </section>
</template>

<style scoped>
.api-key-value {
  width: 100%;
  max-width: 40rem;
  font-family: monospace;
}
</style>
