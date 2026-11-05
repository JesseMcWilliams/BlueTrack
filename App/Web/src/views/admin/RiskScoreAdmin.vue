<script setup>
// D-349: Admin > Risk Score (was Risk Score Bands): Bands (RiskScoreBands.vue, unchanged), Defaults (the default
// scores and each kind of finding's risk value) and Findings (what the findings are now). The tab is in the address
// (?tab=defaults), so a link or a reload opens it; Bands has none, so /admin/risk-score-bands is still Bands.
import { computed, nextTick } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import RiskScoreBands from './RiskScoreBands.vue'
import RiskScoreDefaults from './RiskScoreDefaults.vue'
import RiskFindings from './RiskFindings.vue'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()

const tabs = [
  { key: 'bands', labelKey: 'admin.riskScoreAdmin.tabBands' },
  { key: 'defaults', labelKey: 'admin.riskScoreAdmin.tabDefaults' },
  { key: 'findings', labelKey: 'admin.riskScoreAdmin.tabFindings' }
]

const activeTab = computed(() => (tabs.some(tab => tab.key === route.query.tab) ? route.query.tab : 'bands'))

function select(key) {
  const query = { ...route.query }
  if (key === 'bands') delete query.tab
  else query.tab = key
  router.replace({ query })
}

function onTabKeydown(event) {
  const currentIndex = tabs.findIndex(tab => tab.key === activeTab.value)
  let targetIndex = null
  if (event.key === 'ArrowRight') targetIndex = (currentIndex + 1) % tabs.length
  else if (event.key === 'ArrowLeft') targetIndex = (currentIndex - 1 + tabs.length) % tabs.length
  else if (event.key === 'Home') targetIndex = 0
  else if (event.key === 'End') targetIndex = tabs.length - 1
  else return
  event.preventDefault()
  select(tabs[targetIndex].key)
  nextTick(() => document.getElementById(`risk-score-tab-${tabs[targetIndex].key}`)?.focus())
}
</script>

<template>
  <div>
    <h2>{{ t('admin.riskScoreAdmin.title') }}</h2>
    <div role="tablist" class="risk-score-tabs" :aria-label="t('admin.riskScoreAdmin.title')">
      <button
        v-for="tab in tabs"
        :id="`risk-score-tab-${tab.key}`"
        :key="tab.key"
        role="tab"
        type="button"
        class="risk-score-tab"
        :class="{ 'risk-score-tab--active': activeTab === tab.key }"
        :aria-selected="activeTab === tab.key"
        :aria-controls="`risk-score-panel-${tab.key}`"
        :tabindex="activeTab === tab.key ? 0 : -1"
        @click="select(tab.key)"
        @keydown="onTabKeydown"
      >{{ t(tab.labelKey) }}</button>
    </div>
    <div :id="`risk-score-panel-${activeTab}`" role="tabpanel" :aria-labelledby="`risk-score-tab-${activeTab}`">
      <RiskScoreBands v-if="activeTab === 'bands'" />
      <RiskScoreDefaults v-else-if="activeTab === 'defaults'" />
      <RiskFindings v-else />
    </div>
  </div>
</template>

<style scoped>
.risk-score-tabs {
  display: flex;
  gap: var(--space-1);
  border-bottom: 1px solid var(--color-border);
  margin-bottom: var(--space-3);
}
.risk-score-tab {
  border: none;
  border-bottom: 3px solid transparent;
  border-radius: 0;
  background: transparent;
  margin-right: 0;
  margin-bottom: -1px;
}
.risk-score-tab--active {
  border-bottom-color: var(--color-link);
  font-weight: 700;
}
</style>
