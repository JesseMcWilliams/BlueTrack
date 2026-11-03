<script setup>
// UI review W1-2: this page is for a page the user may not open. A missing address is NotFound.vue. Both offer a way on.
// D-304: the Dashboard needs ViewDashboard too, so the link to it is offered only to those who can open it.
import { useI18n } from 'vue-i18n'
import { useRightsStore } from '../stores/rights'

const { t } = useI18n()
const rights = useRightsStore()
</script>

<template>
  <div>
    <h1>{{ t('shell.accessDenied.title') }}</h1>
    <p>{{ t('shell.accessDenied.message') }}</p>
    <p>
      <template v-if="rights.hasPermission('ViewDashboard')">
        <router-link :to="{ name: 'dashboard' }">{{ t('shell.accessDenied.home') }}</router-link>
        |
      </template>
      <router-link :to="{ name: 'my-profile' }">{{ t('shell.accessDenied.profile') }}</router-link>
    </p>
  </div>
</template>
