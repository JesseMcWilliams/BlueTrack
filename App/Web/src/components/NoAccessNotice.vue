<script setup>
// UI review W1-1: a signed-in user whose account has no roles used to get the whole menu and pages that said only
// "Request failed: 403". This says why, on every page, until a role is given.
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRightsStore } from '../stores/rights'

const { t } = useI18n()
const rights = useRightsStore()
const show = computed(() => rights.loaded && rights.authenticated === true && rights.permissionNames.length === 0)
</script>

<template>
  <p v-if="show" class="no-access-notice" role="alert">
    {{ t('shell.noAccess.message') }}
    <router-link :to="{ name: 'my-profile' }">{{ t('shell.noAccess.profile') }}</router-link>
  </p>
</template>

<style scoped>
.no-access-notice {
  margin: 0;
  padding: var(--space-2) var(--space-4);
  border-bottom: 1px solid var(--color-border);
}
</style>
